using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Rag;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// Retrieval is the only place product scoping is enforced, so these tests treat the
/// scope gate as a security boundary rather than a ranking preference: a session must
/// never be handed guidance written for a product it is not holding, however it got
/// there — direct match or a <c>related</c> link.
/// </summary>
public sealed class KnowledgeIndexTests
{
    private static KnowledgeIndex Build(
        IReadOnlyList<Rag.Knowledge.KnowledgeArticle> articles,
        string[]? enabledFamilies = null,
        RagRetrievalOptions? retrieval = null) =>
        new(
            TestData.LoadResult(articles),
            Options.Create(retrieval ?? new RagRetrievalOptions()),
            Options.Create(new RagOptions { EnabledProductFamilies = enabledFamilies ?? [] }));

    private static KnowledgeRetrievalQuery Query(
        string text, string[]? scope = null, int topK = 3) => new()
    {
        Query = text,
        ScopeTokens = scope?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [],
        TopK = topK,
    };

    // ---------- scope gate ----------

    [Fact]
    public void Universal_articles_are_candidates_without_any_scope()
    {
        var index = Build([TestData.Article("kb-0001", body: "will not power on indicator dark")]);

        var result = index.Search(Query("will not power on"));

        Assert.Single(result.Articles);
    }

    [Fact]
    public void Scoped_article_is_inert_when_no_family_is_enabled()
    {
        // Enabling nothing must not be the same as enabling everything. A deployment that
        // has not opted in should get universal guidance only.
        var index = Build(
            [TestData.Article("kb-0018", body: "second slot not charging amber", scope: ["Vertex Widget"])],
            enabledFamilies: []);

        var result = index.Search(Query("second slot not charging", ["Vertex Widget"]));

        Assert.Empty(result.Articles);
    }

    [Fact]
    public void Scoped_article_is_returned_when_family_is_enabled_and_session_matches()
    {
        var index = Build(
            [TestData.Article("kb-0018", body: "second slot not charging amber", scope: ["Vertex Widget"])],
            enabledFamilies: ["Vertex Widget"]);

        var result = index.Search(Query("second slot not charging", ["Vertex Widget"]));

        Assert.Single(result.Articles);
        Assert.Equal("kb-0018", result.Articles[0].Id);
    }

    [Fact]
    public void Scoped_article_is_excluded_for_a_different_family()
    {
        var index = Build(
            [TestData.Article("kb-0018", body: "second slot not charging amber", scope: ["Vertex Widget"])],
            enabledFamilies: ["Vertex Widget", "Nimbus Beacon"]);

        var result = index.Search(Query("second slot not charging", ["Nimbus Beacon"]));

        Assert.Empty(result.Articles);
    }

    [Fact]
    public void Unscoped_session_gets_no_product_specific_articles()
    {
        var index = Build(
            [
                TestData.Article("kb-0001", body: "will not power on indicator dark"),
                TestData.Article("kb-0018", body: "second slot not charging amber", scope: ["Vertex Widget"]),
            ],
            enabledFamilies: ["Vertex Widget"]);

        var result = index.Search(Query("will not power on", scope: null));

        // The fail-closed direction: an unidentified product gets generic advice only.
        Assert.Equal(["kb-0001"], result.Articles.Select(a => a.Id));
    }

    [Fact]
    public void Reports_corpus_out_of_scope_when_nothing_survives_the_gate()
    {
        var index = Build(
            [TestData.Article("kb-0018", body: "second slot not charging", scope: ["Vertex Widget"])],
            enabledFamilies: ["Vertex Widget"]);

        var result = index.Search(Query("second slot", ["Nimbus Beacon"]));

        Assert.True(result.CorpusOutOfScope);
    }

    // ---------- related-article expansion ----------

    [Fact]
    public void Related_expansion_pulls_in_a_sibling()
    {
        var index = Build(
            [
                TestData.Article("kb-0001", title: "will not power on", related: ["kb-0002"]),
                TestData.Article("kb-0002", title: "cradle not charging", body: "cradle supply fault"),
            ]);

        var result = index.Search(Query("will not power on", topK: 3));

        var ids = result.Articles.Select(a => a.Id).ToList();

        // Expansion is the whole point of a short question: one term should still reach the
        // step-by-step page its neighbour names.
        Assert.Equal("kb-0001", ids[0]);
        Assert.Contains("kb-0002", ids);
    }

    [Fact]
    public void Related_expansion_does_not_cross_product_families()
    {
        var index = Build(
            [
                TestData.Article(
                    "kb-0018",
                    title: "multi-slot cradle second unit",
                    body: "multi-slot second unit queue docks oxidation",
                    scope: ["Vertex Widget"],
                    related: ["kb-0002", "kb-0019"]),
                TestData.Article("kb-0002", title: "cradle not charging", body: "cradle supply fault"),
                TestData.Article(
                    "kb-0019",
                    title: "beacon stalled dock",
                    body: "stalled dock pogo pins amber",
                    scope: ["Nimbus Beacon"]),
            ],
            enabledFamilies: ["Vertex Widget", "Nimbus Beacon"]);

        var result = index.Search(
            Query("multi-slot second unit queue docks oxidation", ["Vertex Widget"], topK: 3));

        var ids = result.Articles.Select(a => a.Id).ToList();

        Assert.Equal("kb-0018", ids[0]);
        Assert.Contains("kb-0002", ids);
        Assert.DoesNotContain("kb-0019", ids);
    }

    [Fact]
    public void Related_expansion_ignores_ids_that_do_not_exist()
    {
        var index = Build(
            [
                TestData.Article("kb-0001", title: "will not power on", related: ["kb-9999"]),
                TestData.Article("kb-0002", title: "cradle not charging", body: "cradle supply fault"),
            ]);

        var result = index.Search(Query("will not power on", topK: 3));

        Assert.DoesNotContain(result.Articles, a => a.Id == "kb-9999");
    }

    [Fact]
    public void Expansion_respects_the_top_k_ceiling()
    {
        var index = Build(
            [
                TestData.Article("kb-0001", title: "alpha fault", related: ["kb-0002", "kb-0003", "kb-0004"]),
                TestData.Article("kb-0002", title: "beta fault", body: "beta detail"),
                TestData.Article("kb-0003", title: "gamma fault", body: "gamma detail"),
                TestData.Article("kb-0004", title: "delta fault", body: "delta detail"),
            ]);

        var result = index.Search(Query("alpha fault", topK: 2));

        Assert.Equal(2, result.Articles.Count);
    }

    [Fact]
    public void Expansion_can_be_switched_off_by_configuration()
    {
        // The sibling shares no vocabulary with the query, so it can only reach the result
        // through the related link. That makes the setting observable at all: with a
        // lexically similar sibling the two runs would look identical either way.
        var index = Build(
            [
                TestData.Article("kb-0001", title: "alpha fault", body: "alpha fault", related: ["kb-0002"]),
                TestData.Article("kb-0002", title: "zzzz", body: "qqqq wwww eeee"),
            ],
            retrieval: new RagRetrievalOptions { MaxRelatedExpansions = 0 });

        var result = index.Search(Query("alpha fault", topK: 3));

        Assert.DoesNotContain(result.Articles, a => a.Id == "kb-0002");
    }

    [Fact]
    public void Expansion_pulls_in_a_sibling_with_no_lexical_overlap()
    {
        var index = Build(
            [
                TestData.Article("kb-0001", title: "alpha fault", body: "alpha fault", related: ["kb-0002"]),
                TestData.Article("kb-0002", title: "zzzz", body: "qqqq wwww eeee"),
            ]);

        var result = index.Search(Query("alpha fault", topK: 3));

        Assert.Contains(result.Articles, a => a.Id == "kb-0002");
    }

    // ---------- general retrieval behaviour ----------

    [Fact]
    public void Returns_nothing_for_a_query_matching_nothing()
    {
        var index = Build([TestData.Article("kb-0001", title: "power fault", body: "indicator dark")]);

        var result = index.Search(Query("zzzz completely unrelated qqqq"));

        Assert.Empty(result.Articles);
    }

    [Fact]
    public void Snippet_is_present_and_bounded()
    {
        var longBody = string.Join(" ", Enumerable.Repeat("fault detail sentence", 500));
        var index = Build([TestData.Article("kb-0001", title: "power fault", body: longBody)]);

        var result = index.Search(Query("power fault", topK: 1));

        var article = Assert.Single(result.Articles);
        Assert.NotNull(article.Snippet);
        Assert.True(
            article.Snippet.Length <= RagRetrievalOptions.MaxSnippetCharacters,
            $"snippet was {article.Snippet.Length} chars");
    }

    [Fact]
    public void Scope_matching_tolerates_a_longer_session_token()
    {
        // Sessions carry whatever the catalogue gave them ("Vertex Widget Pro"), while the
        // article names the family it was written for.
        var index = Build(
            [TestData.Article("kb-0018", body: "second slot not charging", scope: ["Vertex Widget"])],
            enabledFamilies: ["Vertex Widget"]);

        var result = index.Search(Query("second slot not charging", ["Vertex Widget Pro"]));

        Assert.Single(result.Articles);
    }
}
