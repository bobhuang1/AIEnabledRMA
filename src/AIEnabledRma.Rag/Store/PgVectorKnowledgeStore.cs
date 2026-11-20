using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AIEnabledRma.Domain.Knowledge;
using Npgsql;
using NpgsqlTypes;

namespace AIEnabledRma.Rag.Store;

/// <summary>A document to index: an article or a redacted CRM case, already chunked.</summary>
public sealed record KbDocumentInput
{
    public required string SourceId { get; init; }

    /// <summary><see cref="KbKinds.Article"/> or <see cref="KbKinds.CrmCase"/>.</summary>
    public required string Kind { get; init; }

    public required string Title { get; init; }

    public required string Category { get; init; }

    public required IReadOnlyList<string> ProductScope { get; init; }

    public string? ReviewedBy { get; init; }

    public DateOnly? ReviewedAt { get; init; }

    public required string ContentHash { get; init; }

    public string? PolicyVersion { get; init; }

    /// <summary>What a citation to this document shows, e.g. "Resolved case (AX-200, 2026-08)".</summary>
    public string? CitationLabel { get; init; }

    public required IReadOnlyList<KnowledgeChunk> Chunks { get; init; }
}

public static class KbKinds
{
    public const string Article = "article";
    public const string CrmCase = "crm_case";
}

public sealed record KbIngestReport
{
    public required string Kind { get; init; }

    public int Generation { get; init; }

    public bool Unchanged { get; init; }

    public bool Activated { get; init; }

    public int Documents { get; init; }

    public int Chunks { get; init; }

    public int Embedded { get; init; }

    public int Reused { get; init; }

    public int Failed { get; init; }
}

/// <summary>One chunk returned by a hybrid search, with its fused rank score.</summary>
public sealed record KbChunkHit
{
    public required string SourceId { get; init; }

    public required string Kind { get; init; }

    public required string Title { get; init; }

    public required string Category { get; init; }

    public string? CitationLabel { get; init; }

    public required int Ordinal { get; init; }

    public required string HeadingPath { get; init; }

    public required string Text { get; init; }

    public double Score { get; init; }
}

/// <summary>
/// The knowledge store in PostgreSQL with pgvector: documents and chunks with an HNSW cosine index
/// and a GIN full-text index, published in generations so a re-ingest swaps atomically.
/// </summary>
/// <remarks>
/// The schema is created here (<see cref="EnsureSchemaAsync"/>, run by <c>DbAdmin kb-ingest</c>) and
/// not by the EF migrations, so the application schema keeps working on a PostgreSQL without the
/// vector extension. Every statement is parameterised; the only value spliced into SQL is the
/// configured vector dimension, an integer.
/// </remarks>
public sealed partial class PgVectorKnowledgeStore(NpgsqlDataSource dataSource, IEmbeddingModel embedder)
{
    /// <summary>Reciprocal-rank-fusion constant (the usual 60).</summary>
    public const int RrfK = 60;

    private const int CandidatesPerList = 30;

    [GeneratedRegex("[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    public IEmbeddingModel Embedder => embedder;

    public async Task EnsureSchemaAsync(bool rebuild, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        if (rebuild)
        {
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS kb_chunks; DROP TABLE IF EXISTS kb_documents; DROP TABLE IF EXISTS kb_meta;", cancellationToken);
        }

        await ExecuteAsync(connection, "CREATE EXTENSION IF NOT EXISTS vector;", cancellationToken);
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS kb_meta (key text PRIMARY KEY, value text NOT NULL);", cancellationToken);

        var model = await MetaAsync(connection, "embedding_model", cancellationToken);
        var dimensions = await MetaAsync(connection, "embedding_dimensions", cancellationToken);
        if (model is not null && (model != embedder.Name || dimensions != embedder.Dimensions.ToString(CultureInfo.InvariantCulture)))
        {
            throw new InvalidOperationException(
                $"The knowledge store was built with '{model}' ({dimensions} dimensions) but the configured "
                + $"embedding model is '{embedder.Name}' ({embedder.Dimensions}). Re-run kb-ingest with --rebuild.");
        }

        var dim = embedder.Dimensions.ToString(CultureInfo.InvariantCulture);
        await ExecuteAsync(connection, $"""
            CREATE TABLE IF NOT EXISTS kb_documents (
                id uuid PRIMARY KEY,
                generation integer NOT NULL,
                source_id text NOT NULL,
                kind text NOT NULL CHECK (kind IN ('article', 'crm_case')),
                title text NOT NULL,
                category text NOT NULL,
                product_scope text[] NOT NULL,
                reviewed_by text,
                reviewed_at date,
                content_hash text NOT NULL,
                policy_version text,
                citation_label text,
                created_at timestamptz NOT NULL DEFAULT now(),
                UNIQUE (kind, generation, source_id)
            );
            CREATE TABLE IF NOT EXISTS kb_chunks (
                id uuid PRIMARY KEY,
                document_id uuid NOT NULL REFERENCES kb_documents(id) ON DELETE CASCADE,
                ordinal integer NOT NULL,
                heading_path text NOT NULL,
                text text NOT NULL,
                token_count integer NOT NULL,
                chunk_hash text NOT NULL,
                embedding vector({dim}),
                embedding_status text NOT NULL DEFAULT 'pending' CHECK (embedding_status IN ('pending', 'ok', 'failed')),
                tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', heading_path || ' ' || text)) STORED,
                UNIQUE (document_id, ordinal)
            );
            CREATE INDEX IF NOT EXISTS ix_kb_chunks_embedding ON kb_chunks USING hnsw (embedding vector_cosine_ops);
            CREATE INDEX IF NOT EXISTS ix_kb_chunks_tsv ON kb_chunks USING gin (tsv);
            CREATE INDEX IF NOT EXISTS ix_kb_chunks_hash ON kb_chunks (chunk_hash);
            CREATE INDEX IF NOT EXISTS ix_kb_documents_scope ON kb_documents USING gin (product_scope);
            """, cancellationToken);

        await SetMetaAsync(connection, null, "embedding_model", embedder.Name, cancellationToken);
        await SetMetaAsync(connection, null, "embedding_dimensions", dim, cancellationToken);
    }

    /// <summary>
    /// Publishes <paramref name="documents"/> as the new generation of <paramref name="kind"/>.
    /// Idempotent by content hash: an identical set is a no-op, and a chunk whose text (and model)
    /// is unchanged reuses its stored vector. A chunk that cannot be embedded is marked failed and
    /// the generation is not activated, so the previous one stays in service; the next run
    /// retries only what is missing.
    /// </summary>
    public async Task<KbIngestReport> IngestAsync(string kind, IReadOnlyList<KbDocumentInput> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Any(d => d.Kind != kind))
        {
            throw new ArgumentException($"Every document must be of kind '{kind}'.", nameof(documents));
        }

        if (documents.Select(d => d.SourceId).Distinct(StringComparer.Ordinal).Count() != documents.Count)
        {
            throw new ArgumentException("Source ids must be unique.", nameof(documents));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var active = int.Parse(await MetaAsync(connection, ActiveKey(kind), cancellationToken) ?? "0", CultureInfo.InvariantCulture);

        if (active > 0 && await IsSameAsActiveAsync(connection, kind, active, documents, cancellationToken))
        {
            return new KbIngestReport { Kind = kind, Generation = active, Unchanged = true, Activated = true, Documents = documents.Count };
        }

        int generation;
        await using (var cmd = new NpgsqlCommand("SELECT COALESCE(MAX(generation), 0) + 1 FROM kb_documents WHERE kind = @kind", connection))
        {
            cmd.Parameters.AddWithValue("kind", kind);
            generation = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        // Rows for the new generation, inactive until every chunk has a vector.
        var pending = new List<(Guid Id, string Hash, string IndexText)>();
        await using (var tx = await connection.BeginTransactionAsync(cancellationToken))
        {
            foreach (var document in documents)
            {
                var documentId = Guid.NewGuid();
                await using (var insert = new NpgsqlCommand("""
                    INSERT INTO kb_documents (id, generation, source_id, kind, title, category, product_scope,
                        reviewed_by, reviewed_at, content_hash, policy_version, citation_label)
                    VALUES (@id, @generation, @source, @kind, @title, @category, @scope, @reviewedBy, @reviewedAt,
                        @hash, @policy, @label)
                    """, connection, tx))
                {
                    insert.Parameters.AddWithValue("id", documentId);
                    insert.Parameters.AddWithValue("generation", generation);
                    insert.Parameters.AddWithValue("source", document.SourceId);
                    insert.Parameters.AddWithValue("kind", kind);
                    insert.Parameters.AddWithValue("title", document.Title);
                    insert.Parameters.AddWithValue("category", document.Category);
                    insert.Parameters.AddWithValue("scope", document.ProductScope.Select(NormaliseScope).ToArray());
                    insert.Parameters.AddWithValue("reviewedBy", (object?)document.ReviewedBy ?? DBNull.Value);
                    insert.Parameters.AddWithValue("reviewedAt", NpgsqlDbType.Date, (object?)document.ReviewedAt ?? DBNull.Value);
                    insert.Parameters.AddWithValue("hash", document.ContentHash);
                    insert.Parameters.AddWithValue("policy", (object?)document.PolicyVersion ?? DBNull.Value);
                    insert.Parameters.AddWithValue("label", (object?)document.CitationLabel ?? DBNull.Value);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var chunk in document.Chunks)
                {
                    var chunkId = Guid.NewGuid();
                    var hash = ChunkHash(chunk.IndexText, embedder.Name);
                    await using var insert = new NpgsqlCommand("""
                        INSERT INTO kb_chunks (id, document_id, ordinal, heading_path, text, token_count, chunk_hash)
                        VALUES (@id, @document, @ordinal, @path, @text, @tokens, @hash)
                        """, connection, tx);
                    insert.Parameters.AddWithValue("id", chunkId);
                    insert.Parameters.AddWithValue("document", documentId);
                    insert.Parameters.AddWithValue("ordinal", chunk.Ordinal);
                    insert.Parameters.AddWithValue("path", chunk.HeadingPath);
                    insert.Parameters.AddWithValue("text", chunk.Text);
                    insert.Parameters.AddWithValue("tokens", chunk.TokenCount);
                    insert.Parameters.AddWithValue("hash", hash);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                    pending.Add((chunkId, hash, chunk.IndexText));
                }
            }

            await tx.CommitAsync(cancellationToken);
        }

        // Reuse vectors for unchanged chunks (any generation, same model).
        var reused = 0;
        await using (var reuse = new NpgsqlCommand("""
            UPDATE kb_chunks AS target
            SET embedding = source.embedding, embedding_status = 'ok'
            FROM (
                SELECT DISTINCT ON (chunk_hash) chunk_hash, embedding
                FROM kb_chunks
                WHERE embedding_status = 'ok' AND chunk_hash = ANY(@hashes)
            ) AS source
            WHERE target.chunk_hash = source.chunk_hash AND target.id = ANY(@ids)
            """, connection))
        {
            reuse.Parameters.AddWithValue("hashes", pending.Select(p => p.Hash).Distinct().ToArray());
            reuse.Parameters.AddWithValue("ids", pending.Select(p => p.Id).ToArray());
            reused = await reuse.ExecuteNonQueryAsync(cancellationToken);
        }

        var missing = new List<(Guid Id, string Hash, string IndexText)>();
        await using (var query = new NpgsqlCommand("SELECT id FROM kb_chunks WHERE id = ANY(@ids) AND embedding_status <> 'ok'", connection))
        {
            query.Parameters.AddWithValue("ids", pending.Select(p => p.Id).ToArray());
            var ids = new HashSet<Guid>();
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }

            missing.AddRange(pending.Where(p => ids.Contains(p.Id)));
        }

        var embedded = 0;
        var failed = 0;
        foreach (var batch in missing.Chunk(32))
        {
            IReadOnlyList<float[]>? vectors = null;
            try
            {
                vectors = await embedder.EmbedAsync(batch.Select(b => b.IndexText).ToList(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                vectors = null;
            }

            for (var i = 0; i < batch.Length; i++)
            {
                await using var update = new NpgsqlCommand(
                    vectors is null
                        ? "UPDATE kb_chunks SET embedding_status = 'failed' WHERE id = @id"
                        : "UPDATE kb_chunks SET embedding = @vector::vector, embedding_status = 'ok' WHERE id = @id",
                    connection);
                update.Parameters.AddWithValue("id", batch[i].Id);
                if (vectors is not null)
                {
                    update.Parameters.AddWithValue("vector", VectorLiteral(vectors[i]));
                }

                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            if (vectors is null)
            {
                failed += batch.Length;
            }
            else
            {
                embedded += batch.Length;
            }
        }

        var activated = false;
        await using (var tx = await connection.BeginTransactionAsync(cancellationToken))
        {
            if (failed == 0)
            {
                await SetMetaAsync(connection, tx, ActiveKey(kind), generation.ToString(CultureInfo.InvariantCulture), cancellationToken);
                await using var prune = new NpgsqlCommand("DELETE FROM kb_documents WHERE kind = @kind AND generation <> @generation", connection, tx);
                prune.Parameters.AddWithValue("kind", kind);
                prune.Parameters.AddWithValue("generation", generation);
                await prune.ExecuteNonQueryAsync(cancellationToken);
                activated = true;
            }
            else
            {
                // Keep the active generation and this attempt (its good vectors are reused on the
                // next run); drop any older failed attempts.
                await using var prune = new NpgsqlCommand(
                    "DELETE FROM kb_documents WHERE kind = @kind AND generation NOT IN (@generation, @active)", connection, tx);
                prune.Parameters.AddWithValue("kind", kind);
                prune.Parameters.AddWithValue("generation", generation);
                prune.Parameters.AddWithValue("active", active);
                await prune.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }

        return new KbIngestReport
        {
            Kind = kind,
            Generation = generation,
            Activated = activated,
            Documents = documents.Count,
            Chunks = pending.Count,
            Embedded = embedded,
            Reused = reused,
            Failed = failed,
        };
    }

    /// <summary>
    /// Hybrid search over the active generations: full-text (OR of the query's words, ranked by
    /// ts_rank_cd) and vector (cosine) candidate lists, each filtered by product scope in SQL
    /// before ranking, fused by reciprocal rank.
    /// </summary>
    public async Task<IReadOnlyList<KbChunkHit>> SearchAsync(
        string query,
        IReadOnlyCollection<string> allowedScopes,
        int limit,
        CancellationToken cancellationToken)
    {
        var words = WordRegex().Matches(query.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 2).Distinct().Take(64).ToList();
        var vector = (await embedder.EmbedAsync([query], cancellationToken))[0];

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string scoped = """
            FROM kb_chunks c
            JOIN kb_documents d ON d.id = c.document_id
            JOIN kb_meta m ON m.key = 'active_generation:' || d.kind AND m.value = d.generation::text
            WHERE ('*' = ANY(d.product_scope) OR d.product_scope && @allowed)
            """;
        const string columns = "d.source_id, d.kind, d.title, d.category, d.citation_label, c.ordinal, c.heading_path, c.text";

        var lexical = new List<KbChunkHit>();
        if (words.Count > 0)
        {
            await using var cmd = new NpgsqlCommand($"""
                SELECT {columns}
                {scoped} AND c.tsv @@ to_tsquery('english', @tsquery)
                ORDER BY ts_rank_cd(c.tsv, to_tsquery('english', @tsquery)) DESC, d.source_id, c.ordinal
                LIMIT {CandidatesPerList}
                """, connection);
            cmd.Parameters.AddWithValue("allowed", allowedScopes.Select(NormaliseScope).ToArray());
            cmd.Parameters.AddWithValue("tsquery", string.Join(" | ", words));
            lexical.AddRange(await ReadHitsAsync(cmd, cancellationToken));
        }

        var semantic = new List<KbChunkHit>();
        await using (var cmd = new NpgsqlCommand($"""
            SELECT {columns}
            {scoped} AND c.embedding_status = 'ok'
            ORDER BY c.embedding <=> @vector::vector, d.source_id, c.ordinal
            LIMIT {CandidatesPerList}
            """, connection))
        {
            cmd.Parameters.AddWithValue("allowed", allowedScopes.Select(NormaliseScope).ToArray());
            cmd.Parameters.AddWithValue("vector", VectorLiteral(vector));
            semantic.AddRange(await ReadHitsAsync(cmd, cancellationToken));
        }

        var fused = new Dictionary<(string, string, int), (KbChunkHit Hit, double Score)>();
        foreach (var list in new[] { lexical, semantic })
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var hit = list[rank];
                var key = (hit.Kind, hit.SourceId, hit.Ordinal);
                var add = 1d / (RrfK + rank + 1);
                fused[key] = fused.TryGetValue(key, out var existing) ? (existing.Hit, existing.Score + add) : (hit, add);
            }
        }

        return fused.Values
            .OrderByDescending(v => v.Score)
            .ThenBy(v => v.Hit.SourceId, StringComparer.Ordinal)
            .ThenBy(v => v.Hit.Ordinal)
            .Take(limit)
            .Select(v => v.Hit with { Score = v.Score })
            .ToList();
    }

    /// <summary>Number of active documents visible under the scope filter.</summary>
    public async Task<int> CountInScopeAsync(IReadOnlyCollection<string> allowedScopes, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM kb_documents d
            JOIN kb_meta m ON m.key = 'active_generation:' || d.kind AND m.value = d.generation::text
            WHERE ('*' = ANY(d.product_scope) OR d.product_scope && @allowed)
            """, connection);
        cmd.Parameters.AddWithValue("allowed", allowedScopes.Select(NormaliseScope).ToArray());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a store setting (watermarks and the like).</summary>
    public async Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await MetaAsync(connection, key, cancellationToken);
    }

    public async Task SetMetaAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await SetMetaAsync(connection, null, key, value, cancellationToken);
    }

    /// <summary>
    /// The documents of the active generation of <paramref name="kind"/>, with their chunks, so an
    /// incremental run can merge new documents into them and publish the result as a generation.
    /// </summary>
    public async Task<IReadOnlyList<KbDocumentInput>> LoadActiveDocumentsAsync(string kind, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            SELECT d.id, d.source_id, d.title, d.category, d.product_scope, d.reviewed_by, d.reviewed_at,
                   d.content_hash, d.policy_version, d.citation_label,
                   c.ordinal, c.heading_path, c.text, c.token_count
            FROM kb_documents d
            JOIN kb_meta m ON m.key = 'active_generation:' || d.kind AND m.value = d.generation::text
            JOIN kb_chunks c ON c.document_id = d.id
            WHERE d.kind = @kind
            ORDER BY d.source_id, c.ordinal
            """, connection);
        cmd.Parameters.AddWithValue("kind", kind);

        var documents = new List<KbDocumentInput>();
        var chunks = new List<KnowledgeChunk>();
        KbDocumentInput? current = null;
        var currentId = Guid.Empty;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            if (id != currentId)
            {
                if (current is not null)
                {
                    documents.Add(current with { Chunks = chunks.ToList() });
                }

                chunks.Clear();
                currentId = id;
                current = new KbDocumentInput
                {
                    SourceId = reader.GetString(1),
                    Kind = kind,
                    Title = reader.GetString(2),
                    Category = reader.GetString(3),
                    ProductScope = reader.GetFieldValue<string[]>(4),
                    ReviewedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ReviewedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateOnly>(6),
                    ContentHash = reader.GetString(7),
                    PolicyVersion = reader.IsDBNull(8) ? null : reader.GetString(8),
                    CitationLabel = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Chunks = [],
                };
            }

            chunks.Add(new KnowledgeChunk(reader.GetInt32(10), reader.GetString(11), reader.GetString(12), reader.GetInt32(13)));
        }

        if (current is not null)
        {
            documents.Add(current with { Chunks = chunks.ToList() });
        }

        return documents;
    }

    public static string ChunkHash(string indexText, string modelName) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(modelName + "\n" + indexText)));

    public static string VectorLiteral(float[] vector) =>
        "[" + string.Join(',', vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private static string NormaliseScope(string entry) => entry.Trim().ToLowerInvariant();

    private static string ActiveKey(string kind) => "active_generation:" + kind;

    private static async Task<List<KbChunkHit>> ReadHitsAsync(NpgsqlCommand cmd, CancellationToken cancellationToken)
    {
        var hits = new List<KbChunkHit>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new KbChunkHit
            {
                SourceId = reader.GetString(0),
                Kind = reader.GetString(1),
                Title = reader.GetString(2),
                Category = reader.GetString(3),
                CitationLabel = reader.IsDBNull(4) ? null : reader.GetString(4),
                Ordinal = reader.GetInt32(5),
                HeadingPath = reader.GetString(6),
                Text = reader.GetString(7),
            });
        }

        return hits;
    }

    private static async Task<bool> IsSameAsActiveAsync(
        NpgsqlConnection connection,
        string kind,
        int active,
        IReadOnlyList<KbDocumentInput> documents,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT d.source_id, d.content_hash,
                   bool_and(c.embedding_status = 'ok') AS complete
            FROM kb_documents d LEFT JOIN kb_chunks c ON c.document_id = d.id
            WHERE d.kind = @kind AND d.generation = @generation
            GROUP BY d.source_id, d.content_hash
            """, connection);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("generation", active);

        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(2) && !reader.GetBoolean(2))
            {
                return false;
            }

            stored[reader.GetString(0)] = reader.GetString(1);
        }

        return stored.Count == documents.Count
            && documents.All(d => stored.TryGetValue(d.SourceId, out var hash) && hash == d.ContentHash);
    }

    private static async Task<string?> MetaAsync(NpgsqlConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("SELECT value FROM kb_meta WHERE key = @key", connection);
        cmd.Parameters.AddWithValue("key", key);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task SetMetaAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, string key, string value, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO kb_meta (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value",
            connection,
            tx);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("value", value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
