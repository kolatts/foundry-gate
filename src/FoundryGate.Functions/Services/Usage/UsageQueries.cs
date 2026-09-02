namespace FoundryGate.Functions.Services.Usage;

/// <summary>
/// The reconciliation KQL, loaded from the <c>Kql/*.kql</c> files embedded in this assembly.
/// </summary>
/// <remarks>
/// A real <c>.kql</c> file rather than a C# string literal, because #84's acceptance list asks for a
/// query "checked into the repo": one an operator can open, paste into the Log Analytics blade, and
/// get the same answer the Function got. Embedding it keeps the deployed artifact self-contained (no
/// content file to forget in the publish profile) while the text stays reviewable in a diff.
/// </remarks>
public static class UsageQueries
{
    private static readonly Lazy<string> PerSubscriptionTokensQuery =
        new(() => Read("UsageBySubscription.kql"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Per-<c>ApimSubscriptionId</c> prompt/completion/total token sums over the query's time range.</summary>
    public static string PerSubscriptionTokens => PerSubscriptionTokensQuery.Value;

    private static string Read(string fileName)
    {
        var assembly = typeof(UsageQueries).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith($".{fileName}", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded KQL resource '{fileName}' was not found in {assembly.GetName().Name}. Kql/*.kql must stay an <EmbeddedResource> in FoundryGate.Functions.csproj.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded KQL resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
