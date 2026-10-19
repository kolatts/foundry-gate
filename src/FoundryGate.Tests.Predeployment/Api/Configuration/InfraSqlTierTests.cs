using System.Text.RegularExpressions;

namespace FoundryGate.Tests.Predeployment.Api.Configuration;

/// <summary>
/// Azure SQL stays on a PROVISIONED tier in every environment, and every environment's max size
/// fits the tier it asked for.
/// <para>
/// Dev shipped serverless <c>GP_S_Gen5</c> with a 60-minute auto-pause delay on 2026-09-05 and
/// never paused once: <c>UsageSyncFunction</c> runs on <c>0 */15 * * * *</c>, so the database was
/// reconnected four times an hour and the delay never elapsed. It billed a full vCore around the
/// clock — $166 in 16 days, on track for ~$315/month, for a database holding 33 MB — while every
/// resource reported healthy and no test was any the wiser (#277).
/// </para>
/// <para>
/// The lesson is not "pick a longer pause delay". It is that a serverless bill is a property of
/// whatever happens to touch the database, which lives in a different project, on a schedule
/// somebody can change without ever thinking about SQL. A provisioned bill is a property of these
/// files, which is the kind of thing a test can hold still. Hence: provisioned only.
/// </para>
/// </summary>
public class InfraSqlTierTests
{
    /// <summary>Basic tops out at 2 GB; ARM fails the deployment rather than clamping a larger request.</summary>
    private const long BasicMaxSizeBytes = 2L * 1024 * 1024 * 1024;

    private const string BasicSkuName = "Basic";

    private const string SkuDefaultDeclaration = @"param\s+sqlDatabaseSku\s+object\s*=\s*";

    private const string SkuOverrideDeclaration = @"param\s+sqlDatabaseSku\s*=\s*";

    [Fact]
    public void Main_bicep_ships_a_provisioned_sql_sku()
    {
        var sku = ParseSkuName(ReadInfra("main.bicep"), SkuDefaultDeclaration);

        Assert.False(
            IsServerless(sku),
            $"infra/main.bicep defaults sqlDatabaseSku to '{sku}', which is serverless. Auto-pause only saves " +
            "money while nothing touches the database, and the 15-minute UsageSyncFunction timer guarantees " +
            "something does, so the default would bill a vCore around the clock (#277).");
    }

    [Fact]
    public void Every_environment_asks_for_a_provisioned_sku()
    {
        var overrides = ParameterSkuOverrides();

        Assert.NotEmpty(overrides);
        Assert.All(overrides, entry => Assert.False(
            IsServerless(entry.Value),
            $"infra/parameters/{entry.Key} sets sqlDatabaseSku to '{entry.Value}', which is serverless. " +
            "modules/sql.bicep no longer emits autoPauseDelay/minCapacity, so a GP_S_* name there would deploy " +
            "a serverless database on ARM's own defaults and bill a vCore around the clock (#277)."));
    }

    [Fact]
    public void A_basic_database_declares_a_max_size_basic_supports()
    {
        foreach (var (environment, sku, maxSizeBytes) in ResolvedEnvironments())
        {
            if (!string.Equals(sku, BasicSkuName, StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(
                maxSizeBytes <= BasicMaxSizeBytes,
                $"{environment} pairs the Basic SKU with sqlMaxSizeBytes = {maxSizeBytes}, but Basic caps at " +
                $"{BasicMaxSizeBytes} bytes (2 GB). ARM rejects the database rather than clamping the value, so " +
                "this combination fails the deploy outright.");
        }
    }

    [Fact]
    public void The_sql_module_cannot_emit_serverless_properties()
    {
        var sql = ReadInfra(Path.Combine("modules", "sql.bicep"));

        foreach (var property in new[] { "autoPauseDelay", "minCapacity" })
        {
            Assert.False(
                Regex.IsMatch(sql, $@"^\s*{property}\s*:", RegexOptions.Multiline),
                $"modules/sql.bicep assigns '{property}'. That property only applies to serverless SKUs, and " +
                "re-introducing it re-opens the trap in #277: a database whose bill depends on nothing touching " +
                "it, deployed alongside a timer that touches it every 15 minutes.");
        }
    }

    /// <summary>Every environment's effective SKU name and max size, with main.bicep's defaults filled in.</summary>
    private static IEnumerable<(string Environment, string Sku, long MaxSizeBytes)> ResolvedEnvironments()
    {
        var main = ReadInfra("main.bicep");
        var defaultSku = ParseSkuName(main, SkuDefaultDeclaration);
        var defaultMaxSize = ParseLong(main, @"param\s+sqlMaxSizeBytes\s+int\s*=\s*(\d+)");

        yield return ("infra/main.bicep's own defaults", defaultSku, defaultMaxSize);

        foreach (var file in ParameterFiles())
        {
            var text = File.ReadAllText(file);
            var sku = TryParseSkuName(text, SkuOverrideDeclaration) ?? defaultSku;
            var maxSize = TryParseLong(text, @"param\s+sqlMaxSizeBytes\s*=\s*(\d+)") ?? defaultMaxSize;

            yield return ($"infra/parameters/{Path.GetFileName(file)}", sku, maxSize);
        }
    }

    private static IReadOnlyDictionary<string, string> ParameterSkuOverrides()
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in ParameterFiles())
        {
            if (TryParseSkuName(File.ReadAllText(file), SkuOverrideDeclaration) is { } sku)
            {
                overrides[Path.GetFileName(file)] = sku;
            }
        }

        return overrides;
    }

    private static bool IsServerless(string skuName) => skuName.StartsWith("GP_S_", StringComparison.Ordinal);

    private static IEnumerable<string> ParameterFiles() =>
        Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "infra", "parameters"), "*.bicepparam")
            .OrderBy(path => path, StringComparer.Ordinal);

    private static string ParseSkuName(string bicep, string declaration) =>
        TryParseSkuName(bicep, declaration)
        ?? throw new InvalidOperationException($"No sqlDatabaseSku object literal matching /{declaration}/ was found.");

    /// <summary>Reads the <c>name:</c> out of the object literal that follows <paramref name="declaration"/>.</summary>
    private static string? TryParseSkuName(string bicep, string declaration)
    {
        var declarationMatch = Regex.Match(bicep, declaration + @"\{");
        if (!declarationMatch.Success)
        {
            return null;
        }

        var body = MatchBracedBody(bicep, declarationMatch.Index + declarationMatch.Length - 1);
        var name = Regex.Match(body, @"name\s*:\s*'([^']+)'");

        return name.Success ? name.Groups[1].Value : null;
    }

    private static long ParseLong(string bicep, string pattern) =>
        TryParseLong(bicep, pattern) ?? throw new InvalidOperationException($"No value matching /{pattern}/ was found.");

    private static long? TryParseLong(string bicep, string pattern)
    {
        var match = Regex.Match(bicep, pattern);

        return match.Success ? long.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>The text between the brace at <paramref name="openIndex"/> and its partner.</summary>
    private static string MatchBracedBody(string text, int openIndex)
    {
        var depth = 0;

        for (var i = openIndex; i < text.Length; i++)
        {
            depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };

            if (depth == 0)
            {
                return text[(openIndex + 1)..i];
            }
        }

        throw new InvalidOperationException("Unbalanced braces in the Bicep source.");
    }

    private static string ReadInfra(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", relativePath));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FoundryGate.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("FoundryGate.sln was not found above the test binaries.");
    }
}
