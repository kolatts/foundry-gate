using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Api.Configuration;

/// <summary>
/// The gateway's quota tier products and their monthly token caps, bound from <c>Gateway:Tiers</c>.
/// A developer's monthly budget <em>is</em> one of these tiers (D-013): every numeric quota the control
/// plane accepts must equal a configured cap or be unlimited, because APIM's <c>token-quota</c> is a
/// per-product literal (#82) and the tier product a subscription sits on is what the gateway enforces.
/// </summary>
/// <remarks>
/// <para>
/// The shipped values live in <c>appsettings.json</c> and mirror <c>infra/main.bicep</c>'s
/// <c>quotaTiers</c> parameter (<c>GatewayTierOptionsTests</c> cross-checks the two); a fork that
/// changes its tier caps at deploy time overrides them there (or via
/// <c>Gateway__Tiers__{i}__MonthlyTokenQuota</c>). They are deliberately <em>not</em> C# defaults on
/// <see cref="Tiers"/>: the configuration binder appends configured list items to a pre-populated
/// list rather than replacing it, so C# defaults plus a configured override would silently produce
/// duplicate tiers.
/// </para>
/// <para>
/// Validation (fail-fast at startup via <c>ValidateRecursively()</c>, which does <em>not</em> recurse
/// into list items — so <see cref="Validate"/> checks each <see cref="GatewayTier"/> itself): at least
/// one tier; every product id is one of <see cref="GatewayTiers.All"/> (the ids the bicep actually
/// creates); no duplicate ids; every cap is within <c>[0, ValidationConstants.MaxMonthlyTokenQuota]</c>;
/// exactly one unlimited tier (<see cref="GatewayTier.MonthlyTokenQuota"/> = 0); and at least one finite
/// tier for finite quotas to land on.
/// </para>
/// </remarks>
public class GatewayTierOptions : IValidatableObject
{
    /// <summary>The tier products, in any order — resolution sorts finite tiers by cap itself.</summary>
    [Required]
    public List<GatewayTier> Tiers { get; set; } = [];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Tiers.Count == 0)
        {
            yield return new ValidationResult(
                $"{nameof(Tiers)} must contain at least one tier (Gateway:Tiers); the shipped appsettings.json carries the infra/main.bicep defaults.",
                [nameof(Tiers)]);
            yield break;
        }

        // Item-level checks live here because ValidateRecursively() stops at the list: a tier with a
        // negative cap would otherwise start fine as a finite tier no quota ever matches.
        for (var i = 0; i < Tiers.Count; i++)
        {
            var tier = Tiers[i];
            var member = $"{nameof(Tiers)}[{i}]";

            if (string.IsNullOrWhiteSpace(tier.ProductId))
            {
                yield return new ValidationResult($"{member}.{nameof(GatewayTier.ProductId)} is required.", [member]);
            }

            if (tier.MonthlyTokenQuota < 0 || tier.MonthlyTokenQuota > ValidationConstants.MaxMonthlyTokenQuota)
            {
                yield return new ValidationResult(
                    $"{member}.{nameof(GatewayTier.MonthlyTokenQuota)} = {tier.MonthlyTokenQuota} must be between 0 (unlimited) and {ValidationConstants.MaxMonthlyTokenQuota}.",
                    [member]);
            }
        }

        var unknown = Tiers
            .Select(t => t.ProductId)
            .Where(id => !GatewayTiers.All.Contains(id, StringComparer.Ordinal))
            .ToList();
        if (unknown.Count > 0)
        {
            yield return new ValidationResult(
                $"{nameof(Tiers)} names product ids the gateway does not create: {string.Join(", ", unknown)}. Valid ids (FoundryGate.Domain.Constants.GatewayTiers / infra/main.bicep quotaTiers): {string.Join(", ", GatewayTiers.All)}.",
                [nameof(Tiers)]);
        }

        var duplicates = Tiers
            .GroupBy(t => t.ProductId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            yield return new ValidationResult(
                $"{nameof(Tiers)} lists a product id more than once: {string.Join(", ", duplicates)}.",
                [nameof(Tiers)]);
        }

        var unlimitedCount = Tiers.Count(t => t.IsUnlimited);
        if (unlimitedCount != 1)
        {
            yield return new ValidationResult(
                $"{nameof(Tiers)} must contain exactly one unlimited tier ({nameof(GatewayTier.MonthlyTokenQuota)} = 0); found {unlimitedCount}.",
                [nameof(Tiers)]);
        }

        if (Tiers.Count - unlimitedCount == 0)
        {
            yield return new ValidationResult(
                $"{nameof(Tiers)} must contain at least one finite tier ({nameof(GatewayTier.MonthlyTokenQuota)} > 0) for finite quotas to map onto.",
                [nameof(Tiers)]);
        }
    }
}

/// <summary>One quota tier: an APIM product id, its display name, and the monthly cap its <c>llm-token-limit</c> policy enforces.</summary>
public class GatewayTier
{
    /// <summary>APIM product id — one of <see cref="GatewayTiers.All"/> (the <c>name</c> of a bicep <c>quotaTiers</c> entry).</summary>
    [Required]
    [StringLength(64)]
    public string ProductId { get; set; } = string.Empty;

    /// <summary>Human-readable name for the UI (bicep's <c>displayName</c>); falls back to <see cref="ProductId"/> when empty.</summary>
    [StringLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Tokens per calendar month the tier's policy enforces; <c>0</c> means the tier carries no
    /// <c>token-quota</c> at all (unlimited — TPM smoothing only), matching bicep's
    /// <c>monthlyTokenQuota: 0</c> convention.
    /// </summary>
    [Range(0, ValidationConstants.MaxMonthlyTokenQuota)]
    public long MonthlyTokenQuota { get; set; }

    /// <summary>True when this is the unlimited tier (<see cref="MonthlyTokenQuota"/> = 0).</summary>
    public bool IsUnlimited => MonthlyTokenQuota == 0;
}
