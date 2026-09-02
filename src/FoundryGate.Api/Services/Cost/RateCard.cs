using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoundryGate.Api.Services.Cost;

/// <summary>
/// One model's price, per million tokens, in whatever currency the fork bills in (#177). The
/// <see cref="ModelPrefix"/> is matched against a deployment's model name by prefix, so
/// <c>claude-opus</c> covers every Opus deployment without naming each version.
/// </summary>
/// <remarks>
/// The entry whose prefix is <see cref="RateCard.BlendedPrefix"/> (<c>*</c>) is the fallback, and it
/// is the <em>only</em> entry anything reads today — see <see cref="RateCard.BlendedRatePerMillion"/>
/// for why.
/// </remarks>
/// <param name="ModelPrefix">Case-insensitive prefix of the model name, or <c>*</c> for the fallback.</param>
/// <param name="InputPerMillion">Price per million prompt tokens. Non-negative.</param>
/// <param name="OutputPerMillion">Price per million completion tokens. Non-negative.</param>
public record RateCardEntry(
    [property: JsonPropertyName("modelPrefix")] string ModelPrefix,
    [property: JsonPropertyName("inputPerMillion")] decimal InputPerMillion,
    [property: JsonPropertyName("outputPerMillion")] decimal OutputPerMillion);

/// <summary>
/// The parsed <c>RateCard</c> <c>SystemConfiguration</c> value (#177): what a token costs, so the
/// portal can put a number next to a developer's usage.
/// </summary>
/// <remarks>
/// <b>Why this has to be computed at all.</b> Claude bills as a single aggregate Marketplace CCU
/// meter, so Azure Cost Management cannot break spend down per deployment, per subscription or per
/// user (research.md §8). Per-developer cost is therefore <c>tokens × rate card</c>, and the rate
/// card is an operator-maintained fork setting rather than something Azure can be asked for.
/// <para>
/// <b>Why every figure derived from it is an estimate.</b> Three separate reasons, none of which
/// this type can fix:
/// </para>
/// <list type="number">
/// <item><b>There is no prompt/completion split.</b> <c>QuotaAllocation.TokensUsed</c> is one total —
/// the reconciliation job aggregates the gateway's LLM log by subscription, not by direction — so
/// input and output prices cannot be applied separately. See
/// <see cref="BlendedRatePerMillion"/>.</item>
/// <item><b>There is no per-model split either.</b> The same total covers whatever mix of models the
/// developer used, so only the <c>*</c> fallback entry can be applied. The per-model entries are
/// stored and validated ahead of a reader that can use them — <b>#213</b> is that reader: the
/// reconciliation KQL already selects <c>PromptTokens</c>/<c>CompletionTokens</c> and sees
/// <c>ModelName</c> on every row, so both splits are thrown away on the way into the database
/// rather than missing at the source.</item>
/// <item><b>The token totals are themselves a floor.</b> Interrupted streams undercount (#84), and
/// cache-read/creation token weighting at the gateway is unverified (#88).</item>
/// </list>
/// <para>
/// Anything rendered from this must say "estimate". It is a reporting figure and nothing else: the
/// gateway enforces tokens, never dollars.
/// </para>
/// </remarks>
public sealed class RateCard
{
    /// <summary>The <see cref="RateCardEntry.ModelPrefix"/> of the fallback entry — the one blended rate everything uses today.</summary>
    public const string BlendedPrefix = "*";

    /// <summary>An unconfigured rate card: no entries, no estimates. What a fork ships with.</summary>
    public static readonly RateCard Empty = new([]);

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private RateCard(IReadOnlyList<RateCardEntry> entries) => Entries = entries;

    /// <summary>The configured entries, in the order they were written.</summary>
    public IReadOnlyList<RateCardEntry> Entries { get; }

    /// <summary>
    /// The one rate an estimate can honestly use: the mean of the <c>*</c> entry's input and output
    /// prices, or <see langword="null"/> when no <c>*</c> entry is configured (in which case nothing
    /// is estimated, rather than a zero that would read as "free").
    /// </summary>
    /// <remarks>
    /// The mean assumes an even prompt/completion mix, which nothing verifies — the token total
    /// carries no split to check it against. A fork that knows its real mix expresses that by
    /// setting <em>both</em> of the <c>*</c> entry's prices to its own blended number; the mean of
    /// two equal numbers is that number. A third "blended" field would only be a second way to write
    /// the same guess.
    /// </remarks>
    public decimal? BlendedRatePerMillion
    {
        get
        {
            var fallback = Entries.FirstOrDefault(e => string.Equals(e.ModelPrefix, BlendedPrefix, StringComparison.Ordinal));
            return fallback is null ? null : (fallback.InputPerMillion + fallback.OutputPerMillion) / 2m;
        }
    }

    /// <summary>
    /// <paramref name="tokens"/> priced at <see cref="BlendedRatePerMillion"/>, or
    /// <see langword="null"/> when the fork has configured no fallback rate. Rounded to cents.
    /// </summary>
    public decimal? Estimate(long tokens) =>
        BlendedRatePerMillion is { } rate
            ? Math.Round(tokens / 1_000_000m * rate, 2, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>
    /// Parses a stored <c>RateCard</c> value. An empty or whitespace value is
    /// <see cref="Empty"/> — an unconfigured fork is not a broken one.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The value is not a JSON array of entries, an entry has a blank <c>modelPrefix</c>, a price is
    /// negative, or a prefix is repeated. The message states the rule, because it is shown to the
    /// admin editing the row.
    /// </exception>
    public static RateCard Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Empty;
        }

        RateCardEntry[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<RateCardEntry[]>(value, ParseOptions);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"The rate card must be a JSON array of {{ \"modelPrefix\", \"inputPerMillion\", \"outputPerMillion\" }} objects. {exception.Message}",
                nameof(value),
                exception);
        }

        if (entries is null)
        {
            throw new ArgumentException("The rate card must be a JSON array, not null.", nameof(value));
        }

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new ArgumentException("The rate card contains a null entry.", nameof(value));
            }

            if (string.IsNullOrWhiteSpace(entry.ModelPrefix))
            {
                throw new ArgumentException(
                    $"Every rate card entry needs a 'modelPrefix' (a model-name prefix such as 'claude-opus', or '{BlendedPrefix}' for the fallback).",
                    nameof(value));
            }

            if (entry.InputPerMillion < 0 || entry.OutputPerMillion < 0)
            {
                throw new ArgumentException(
                    $"Rate card entry '{entry.ModelPrefix}' has a negative price. 'inputPerMillion' and 'outputPerMillion' must be zero or more.",
                    nameof(value));
            }
        }

        var duplicate = entries
            .GroupBy(e => e.ModelPrefix, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"The rate card lists '{duplicate.Key}' more than once. Each modelPrefix may appear at most once.",
                nameof(value));
        }

        return new RateCard(entries);
    }

    /// <summary>
    /// The canonical form to store: the parsed entries re-serialized compactly, so two admins
    /// pasting the same rates with different whitespace leave the same row behind.
    /// </summary>
    public string ToStoredValue() => JsonSerializer.Serialize(Entries, ParseOptions);

    /// <summary>The rule, as a sentence an admin can act on — appended to validation failures.</summary>
    public static string Describe() =>
        "A rate card is a JSON array of { \"modelPrefix\", \"inputPerMillion\", \"outputPerMillion\" } objects, "
        + $"prices per million tokens and never negative, each prefix at most once. The entry with modelPrefix \"{BlendedPrefix}\" "
        + "is the blended fallback every estimate is computed from; without it no cost is estimated. "
        + $"Example: [{{\"modelPrefix\":\"{BlendedPrefix}\",\"inputPerMillion\":{3m.ToString(CultureInfo.InvariantCulture)},\"outputPerMillion\":{15m.ToString(CultureInfo.InvariantCulture)}}}]";
}
