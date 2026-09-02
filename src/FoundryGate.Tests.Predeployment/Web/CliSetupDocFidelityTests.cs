using System.Text.RegularExpressions;
using Bunit;
using FoundryGate.Domain.Config;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// CLAUDE.md invariant 3: <c>getting-started/cli-setup.mdx</c> carries only empirically verified
/// configuration, and <c>/me</c>'s "Configure your AI CLI" panel is a filled-in copy of it. This
/// reads the fenced code blocks out of the <em>actual doc</em> and asserts the rendered page still
/// says the same thing — a hard-coded copy of the doc in a test drifts silently, which is exactly
/// how the panel and the doc came to disagree about whether <c>model</c> takes an alias or a
/// deployment name.
/// </summary>
public partial class CliSetupDocFidelityTests : WebTestContext
{
    public CliSetupDocFidelityTests()
    {
        SignInAsDeveloper();

        // Aliases named the way the shipped alias map names them (infra/main.bicep
        // productModelAliases), so the substituted snippets are the ones a real developer copies.
        Api.MeResult = ApiCallResult<FoundryGate.Domain.Users.Contracts.UserProfileResponse>.Ok(
            WebTestData.Profile(cliConfig: WebTestData.CliConfig(
                "https://ai.example.test",
                [
                    WebTestData.Alias("sonnet", "claude-sonnet-4-5"),
                    WebTestData.Alias("haiku", "claude-haiku-4-5"),
                    WebTestData.Alias("opus", "claude-opus-4-5"),
                    WebTestData.Alias("gpt", "gpt-4-1-mini", ModelProviderType.OpenAi),
                ])));
    }

    [Fact]
    public void Every_configuration_line_in_the_doc_is_on_the_page()
    {
        var page = RenderPage<Me>();
        var markup = page.Markup;

        var missing = ConfigurationLines()
            .Where(line => !markup.Contains(Substitute(line), StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"""
            These lines are in getting-started/cli-setup.mdx but not on /me's CLI panel. Either the
            page drifted from the doc, or the doc changed and the panel needs updating — CLAUDE.md
            invariant 3 says the doc leads, and only verified configuration goes in either:
            {string.Join(Environment.NewLine, missing)}
            """);
    }

    [Fact]
    public void The_doc_and_the_page_agree_that_model_takes_an_alias()
    {
        // The one substantive disagreement this test was written to catch. infra/main.bicep's
        // productModelAliases IS the per-tier allowlist, so a deployment name in `model` is
        // 403 model_not_permitted — the doc says so, and the panel must not suggest otherwise.
        var doc = ReadDoc();
        Assert.Contains("model_not_permitted", doc, StringComparison.Ordinal);

        var page = RenderPage<Me>();
        Assert.Contains("model_not_permitted", page.Markup, StringComparison.Ordinal);
        Assert.Contains("ANTHROPIC_DEFAULT_SONNET_MODEL=sonnet", page.Markup, StringComparison.Ordinal);
        Assert.Contains("""model = "gpt" """.TrimEnd(), page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_doc_actually_parses_into_configuration_lines()
    {
        // Guards the guard: if the fence parsing ever breaks, the fidelity test above would pass
        // vacuously on an empty list.
        var lines = ConfigurationLines();

        Assert.True(lines.Count >= 8, $"Only {lines.Count} configuration lines were parsed out of cli-setup.mdx.");
        Assert.Contains("export CLAUDE_CODE_USE_FOUNDRY=1", lines);
    }

    /// <summary>
    /// Every substantive line of every fenced block in the doc: comments, blank lines and the
    /// prose around them are skipped, as are the <c>curl</c> smoke-test blocks (they carry
    /// JSON payloads that the page renders identically but whose placeholders the page fills in
    /// per-developer in ways the doc cannot spell).
    /// </summary>
    private static IReadOnlyList<string> ConfigurationLines()
    {
        var doc = ReadDoc();

        return FencedBlock()
            .Matches(doc)
            .Select(m => m.Groups["body"].Value)
            .SelectMany(body => body.Split('\n'))
            .Select(StripTrailingComment)
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith("curl", StringComparison.Ordinal))
            .Where(line => line.Contains('=', StringComparison.Ordinal))
            .Where(line => !line.StartsWith("-H", StringComparison.Ordinal) && !line.StartsWith("-d", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Drops an inline <c>#</c> comment (one outside a quoted value). The comparison is about the
    /// configuration the doc prescribes, not the prose beside it — the doc's comments carry
    /// placeholder hints ("e.g. gpt") the filled-in page has no reason to repeat. The guidance
    /// those comments give is asserted separately, by name, in the test below.
    /// </summary>
    private static string StripTrailingComment(string line)
    {
        var quotes = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quotes++;
            }
            else if (line[i] == '#' && quotes % 2 == 0)
            {
                return line[..i].Trim();
            }
        }

        return line.Trim();
    }

    /// <summary>
    /// Replaces the doc's <c>&lt;placeholders&gt;</c> with the values this test's profile supplies,
    /// so the comparison is against what a developer with that profile actually sees.
    /// </summary>
    private static string Substitute(string line) => line
        .Replace("<gateway-url>", "ai.example.test", StringComparison.Ordinal)
        .Replace("<sonnet-alias>", "sonnet", StringComparison.Ordinal)
        .Replace("<opus-alias>", "opus", StringComparison.Ordinal)
        .Replace("<haiku-alias>", "haiku", StringComparison.Ordinal)
        .Replace("<openai-alias>", "gpt", StringComparison.Ordinal)
        .Replace("<your-key>", "&lt;your-key&gt;", StringComparison.Ordinal);

    private static string ReadDoc()
    {
        var path = Path.Combine(RepositoryRoot(), "docs-site", "src", "content", "docs", "getting-started", "cli-setup.mdx");
        Assert.True(File.Exists(path), $"cli-setup.mdx not found at {path}.");
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FoundryGate.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    [GeneratedRegex("```[a-z]*\\n(?<body>.*?)```", RegexOptions.Singleline)]
    private static partial Regex FencedBlock();
}
