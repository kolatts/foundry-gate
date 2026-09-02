using FoundryGate.Api.Services.Cost;

namespace FoundryGate.Tests.Predeployment.Api.Services.Cost;

/// <summary>
/// The rate card's parsing rules and the one arithmetic it does (#177). A malformed card has to be a
/// message an admin can act on, and the estimate has to be reproducible — this number ends up next
/// to a developer's name.
/// </summary>
public class RateCardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unconfigured_value_is_an_empty_card_that_estimates_nothing(string? value)
    {
        var card = RateCard.Parse(value);

        Assert.Empty(card.Entries);
        Assert.Null(card.BlendedRatePerMillion);

        // Null, not zero: a fork that has not priced its tokens has no cost to report, and a zero
        // would render as "free" on the dashboard.
        Assert.Null(card.Estimate(10_000_000));
    }

    [Fact]
    public void An_empty_array_is_also_an_empty_card()
    {
        var card = RateCard.Parse("[]");

        Assert.Empty(card.Entries);
        Assert.Null(card.Estimate(1));
    }

    [Fact]
    public void Entries_round_trip_through_the_stored_form()
    {
        var card = RateCard.Parse(
            """[{"modelPrefix":"claude-opus","inputPerMillion":15,"outputPerMillion":75},{"modelPrefix":"*","inputPerMillion":3,"outputPerMillion":15}]""");

        Assert.Equal(["claude-opus", "*"], card.Entries.Select(e => e.ModelPrefix));
        Assert.Equal(15m, card.Entries[0].InputPerMillion);
        Assert.Equal(75m, card.Entries[0].OutputPerMillion);

        var reparsed = RateCard.Parse(card.ToStoredValue());
        Assert.Equal(card.Entries, reparsed.Entries);
    }

    [Fact]
    public void The_blended_rate_is_the_mean_of_the_fallback_entrys_two_prices()
    {
        // TokensUsed is one total with no prompt/completion split, so the two prices have to collapse
        // to one number. A fork that knows its real mix sets both to its own blended figure.
        var card = RateCard.Parse("""[{"modelPrefix":"*","inputPerMillion":3,"outputPerMillion":15}]""");

        Assert.Equal(9m, card.BlendedRatePerMillion);
    }

    [Fact]
    public void Per_model_entries_alone_estimate_nothing()
    {
        // Deliberate: without a per-model token split there is no honest way to apply them, so they
        // are stored and validated ahead of a reader rather than silently averaged into a guess.
        var card = RateCard.Parse("""[{"modelPrefix":"claude-opus","inputPerMillion":15,"outputPerMillion":75}]""");

        Assert.Null(card.BlendedRatePerMillion);
        Assert.Null(card.Estimate(1_000_000));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1_000_000, 9)]
    [InlineData(2_500_000, 22.5)]
    [InlineData(1, 0)] // rounds to cents
    public void Estimate_prices_tokens_at_the_blended_rate_per_million(long tokens, decimal expected)
    {
        var card = RateCard.Parse("""[{"modelPrefix":"*","inputPerMillion":3,"outputPerMillion":15}]""");

        Assert.Equal(expected, card.Estimate(tokens));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""[{"modelPrefix":"","inputPerMillion":1,"outputPerMillion":1}]""")]
    [InlineData("""[{"modelPrefix":"*","inputPerMillion":-1,"outputPerMillion":1}]""")]
    [InlineData("""[{"modelPrefix":"*","inputPerMillion":1,"outputPerMillion":-1}]""")]
    [InlineData("""[{"modelPrefix":"*","inputPerMillion":1,"outputPerMillion":1},{"modelPrefix":"*","inputPerMillion":2,"outputPerMillion":2}]""")]
    public void A_malformed_card_is_an_ArgumentException(string value) =>
        Assert.Throws<ArgumentException>(() => RateCard.Parse(value));

    [Theory]
    [InlineData("79228162514264337593543950335")] // decimal.MaxValue — the reviewer's probe
    [InlineData("1000000.01")]
    public void A_price_above_the_ceiling_is_refused(string price)
    {
        // Unbounded above, Parse accepted decimal.MaxValue and BlendedRatePerMillion then overflowed
        // on the addition — turning GET /quota/allocations/me, which every authenticated developer
        // hits, into a 500 until someone edited the row back by hand (#177 review).
        var exception = Assert.Throws<ArgumentException>(() => RateCard.Parse(
            $$"""[{"modelPrefix":"*","inputPerMillion":{{price}},"outputPerMillion":1}]"""));

        Assert.Contains("per million tokens", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ceiling_itself_is_accepted_and_prices_the_largest_possible_usage()
    {
        // The widest estimate this can ever be asked for: the highest price Parse allows over more
        // tokens than any fork could burn. decimal is wide enough that it does not come close, which
        // is why the bound above is enough to make every read path total.
        var card = RateCard.Parse(
            $$"""[{"modelPrefix":"*","inputPerMillion":{{RateCard.MaxPricePerMillion}},"outputPerMillion":{{RateCard.MaxPricePerMillion}}}]""");

        Assert.Equal(RateCard.MaxPricePerMillion, card.BlendedRatePerMillion);
        Assert.NotNull(card.Estimate(long.MaxValue));
    }

    [Fact]
    public void A_repeated_prefix_is_refused_whatever_its_casing()
    {
        var exception = Assert.Throws<ArgumentException>(() => RateCard.Parse(
            """[{"modelPrefix":"claude-opus","inputPerMillion":1,"outputPerMillion":1},{"modelPrefix":"CLAUDE-OPUS","inputPerMillion":2,"outputPerMillion":2}]"""));

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rule_description_names_the_fallback_prefix_so_an_admin_knows_what_to_add()
    {
        var description = RateCard.Describe();

        Assert.Contains(RateCard.BlendedPrefix, description, StringComparison.Ordinal);
        Assert.Contains("inputPerMillion", description, StringComparison.Ordinal);
        Assert.Contains("outputPerMillion", description, StringComparison.Ordinal);
    }
}
