using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoundryGate.Api.Services.Cost;

/// <summary>
/// One entry in the stored rate card: a price per million tokens, in whatever currency the fork
/// bills in (#177).
/// </summary>
/// <remarks>
/// <b>Only the <see cref="RateCard.BlendedPrefix"/> (<c>*</c>) entry is read today, and nothing
/// matches on <see cref="ModelPrefix"/> at all.</b> Applying a per-model price needs a per-model
/// token split, which the control plane does not store — so the named entries are parsed and
/// validated, then ignored, until #213 lands the split that can use them. The field is here rather
/// than added later because it is the shape of the stored configuration, and changing that shape
/// afterwards would mean rewriting every fork's row.
/// </remarks>
/// <param name="ModelPrefix">Names the model this price is for — <c>*</c> for the fallback, the only one anything reads. See the remarks.</param>
/// <param name="InputPerMillion">Price per million prompt tokens. Between 0 and <see cref="RateCard.MaxPricePerMillion"/>.</param>
/// <param name="OutputPerMillion">Price per million completion tokens. Between 0 and <see cref="RateCard.MaxPricePerMillion"/>.</param>
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
/// developer used, so only the <c>*</c> fallback entry can be applied and <c>modelPrefix</c> is
/// matched against nothing. The named entries are validated ahead of a reader — <b>#213</b> is that reader: the
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

    /// <summary>
    /// Ceiling on a single price, per million tokens. A guardrail, not a business rule: no real model
    /// costs a million currency units per million tokens, and without an upper bound
    /// <c>PUT /config/RateCard</c> would accept <c>decimal.MaxValue</c> — which then overflows the
    /// blended-rate addition and turns <c>GET /quota/allocations/me</c>, the endpoint every
    /// authenticated developer hits, into a <c>500</c> until someone edits the row back by hand.
    /// </summary>
    public const decimal MaxPricePerMillion = 1_000_000m;

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
            if (fallback is null)
            {
                return null;
            }

            // MaxPricePerMillion already makes this addition unoverflowable for anything Parse
            // accepts. The catch is for the row Parse never saw — a seed script or a DBA writing
            // SystemConfiguration directly — because this property is on the read path of
            // GET /dashboard, /quota/allocations and /quota/allocations/me, and none of those may
            // fail over a price list. Unknown, not zero: a zero would read as "free".
            try
            {
                return (fallback.InputPerMillion + fallback.OutputPerMillion) / 2m;
            }
            catch (OverflowException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// <paramref name="tokens"/> priced at <see cref="BlendedRatePerMillion"/>, or
    /// <see langword="null"/> when the fork has configured no fallback rate. Rounded to cents.
    /// </summary>
    public decimal? Estimate(long tokens)
    {
        if (BlendedRatePerMillion is not { } rate)
        {
            return null;
        }

        // Same reasoning as BlendedRatePerMillion: a read path may not throw over a stored value.
        // decimal is exact here — no binary rounding drift on money — and wide enough that a bounded
        // rate over long.MaxValue tokens cannot reach its limit, so this only fires for a row that
        // never went through Parse.
        try
        {
            return Math.Round(tokens / 1_000_000m * rate, 2, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

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
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // The two System.Text.Json throws: malformed JSON, and JSON that is well formed but
            // cannot become a RateCardEntry[] (a string where a number belongs). Both are the
            // admin's typo, so both become the ArgumentException PUT /config/{key} maps to a 400 —
            // which also makes ArgumentException Parse's only escape, so CostEstimator's catch is
            // total rather than nearly so.
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

            if (entry.InputPerMillion > MaxPricePerMillion || entry.OutputPerMillion > MaxPricePerMillion)
            {
                throw new ArgumentException(
                    $"Rate card entry '{entry.ModelPrefix}' prices a million tokens above {MaxPricePerMillion:N0}. "
                    + "'inputPerMillion' and 'outputPerMillion' are prices per million tokens, not per token.",
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
        + $"prices per million tokens between 0 and {MaxPricePerMillion:N0}, each modelPrefix at most once. The entry with "
        + $"modelPrefix \"{BlendedPrefix}\" is the blended fallback every estimate is computed from; without it no cost is "
        + "estimated, and no other entry is read yet (issue #213). "
        + $"Example: [{{\"modelPrefix\":\"{BlendedPrefix}\",\"inputPerMillion\":{3m.ToString(CultureInfo.InvariantCulture)},\"outputPerMillion\":{15m.ToString(CultureInfo.InvariantCulture)}}}]";
}
