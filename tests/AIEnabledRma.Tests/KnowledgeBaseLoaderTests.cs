using AIEnabledRma.Rag.Knowledge;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIEnabledRma.Tests;

/// <summary>
/// The loader is the boundary where hand-written markdown becomes something the assistant
/// is willing to cite. These tests pin the rule that a structurally broken article is
/// *reported*, not quietly skipped: an article that vanishes without a record leaves the
/// assistant confidently missing the one page that answered the customer's question.
/// </summary>
public sealed class KnowledgeBaseLoaderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "rag-loader-" + Guid.NewGuid().ToString("N"));

    private readonly KnowledgeBaseLoader _loader =
        new(NullLogger<KnowledgeBaseLoader>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string fileName, string content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private const string ValidArticle = """
        ---
        id: kb-0001
        title: "Unit will not power on"
        category: power
        product_scope: ["*"]
        keywords: ["power", "boot"]
        related: ["kb-0002"]
        ---

        Hold the power button for ten seconds. If the indicator stays dark, the battery
        is flat and needs thirty minutes on the cradle before another attempt.
        """;

    [Fact]
    public void Loads_a_well_formed_article()
    {
        Write("kb-0001.md", ValidArticle);

        var result = _loader.Load(_root);

        Assert.Empty(result.Errors);
        var article = Assert.Single(result.Articles);
        Assert.Equal("kb-0001", article.Id);
        Assert.Equal("Unit will not power on", article.Title);
        Assert.Equal("power", article.Category);
        Assert.True(article.IsUniversal);
        Assert.Equal(["kb-0002"], article.Related);
        Assert.Contains("power", article.Tokens);
    }

    [Fact]
    public void Reports_an_article_with_no_front_matter()
    {
        Write("broken.md", "just some prose with no front matter at all");

        var result = _loader.Load(_root);

        Assert.Empty(result.Articles);
        Assert.Contains(result.Errors, e => e.Contains("front matter", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("title")]
    [InlineData("category")]
    public void Reports_the_specific_missing_required_field(string omitted)
    {
        var fields = new Dictionary<string, string>
        {
            ["id"] = "kb-0001",
            ["title"] = "Some title",
            ["category"] = "power",
        };

        fields.Remove(omitted);

        Write(
            "kb-0001.md",
            "---\n"
            + string.Join("\n", fields.Select(f => $"{f.Key}: {f.Value}"))
            + "\n---\n\nBody text that is long enough to pass the empty-body check.\n");

        var result = _loader.Load(_root);

        Assert.Empty(result.Articles);
        Assert.Contains(result.Errors, e => e.Contains(omitted, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reports_an_article_with_an_empty_body()
    {
        Write("kb-0001.md", "---\nid: kb-0001\ntitle: Empty\ncategory: power\n---\n\n   \n");

        var result = _loader.Load(_root);

        Assert.Empty(result.Articles);
        Assert.Contains(result.Errors, e => e.Contains("empty body", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reports_duplicate_ids_case_insensitively()
    {
        Write("a.md", ValidArticle);
        Write("b.md", ValidArticle.Replace("kb-0001", "KB-0001"));

        var result = _loader.Load(_root);

        // Ids differing only by case still collide, so citation lookup stays unambiguous.
        Assert.Single(result.Articles);
        Assert.Contains(result.Errors, e => e.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reports_a_missing_directory()
    {
        var result = _loader.Load(Path.Combine(_root, "does-not-exist"));

        Assert.Empty(result.Articles);
        Assert.Contains(result.Errors, e => e.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reports_an_empty_directory()
    {
        Directory.CreateDirectory(_root);

        var result = _loader.Load(_root);

        Assert.Empty(result.Articles);
        Assert.Contains(result.Errors, e => e.Contains("No articles", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Skips_the_directory_readme()
    {
        Write("README.md", "# Knowledge base\n\nNot an article, it has no front matter.\n");
        Write("kb-0001.md", ValidArticle);

        var result = _loader.Load(_root);

        Assert.Empty(result.Errors);
        Assert.Single(result.Articles);
    }

    [Fact]
    public void Absent_product_scope_defaults_to_universal()
    {
        Write(
            "kb-0001.md",
            "---\nid: kb-0001\ntitle: T\ncategory: power\n---\n\nA body long enough to load.\n");

        var result = _loader.Load(_root);

        var article = Assert.Single(result.Articles);

        // Over-sharing is the safe direction: a forgotten field must not silently disable
        // an article, and the deployment-level opt-in still gates product-specific content.
        Assert.True(article.IsUniversal);
    }

    [Fact]
    public void Strips_quotes_from_related_ids()
    {
        Write(
            "kb-0001.md",
            "---\nid: kb-0001\ntitle: T\ncategory: power\n"
            + "product_scope: [\"Widget\"]\nrelated: [\"kb-0002\", \"kb-0003\"]\n---\n\nBody.\n");

        var result = _loader.Load(_root);

        var article = Assert.Single(result.Articles);

        Assert.Equal(["kb-0002", "kb-0003"], article.Related);
        Assert.Equal(["Widget"], article.ProductScope);
    }

    [Fact]
    public void One_broken_article_does_not_discard_the_healthy_ones()
    {
        Write("kb-0001.md", ValidArticle);
        Write("kb-broken.md", "no front matter here");

        var result = _loader.Load(_root);

        Assert.Single(result.Articles);
        Assert.Single(result.Errors);
    }
}
