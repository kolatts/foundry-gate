using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using FoundryGate.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace FoundryGate.Tests.Predeployment.Core.Configuration;

/// <summary>
/// Drift alarm between <see cref="GatewayOptions"/> and the <c>Gateway__*</c> environment variables
/// <c>infra/modules/control-plane.bicep</c> actually sets — the question #108 asks and that nothing
/// answered automatically: infra is the source of truth for the names, so a rename on either side, a
/// variable infra sets that nothing binds, or a member the control plane needs that infra never
/// supplies, should all fail here rather than in a deployed environment.
/// </summary>
/// <remarks>
/// <para>
/// <b>It binds rather than asserts.</b> The names are scraped from the bicep, turned into a
/// configuration source with generated values, bound to a real <see cref="GatewayOptions"/>, and then
/// checked <em>by reflection</em> that each scraped member came back carrying what was put in. Hardcoded
/// property assertions would not have tested the alarm's own claim: the configuration binder ignores
/// keys it cannot place, so a `Gateway__Whatever` with nothing behind it binds silently to nothing
/// (#204 review).
/// </para>
/// <para>
/// Deliberately reads the bicep as <em>text</em>, the way <c>SchemaParityTests</c> reads the .sql
/// files: no Bicep/ARM tooling is available in every environment these tests run in, and the repo's
/// own style (<c>{ name: 'Key', value: … }</c>, one per line) parses cleanly. A hand-authored line that
/// strays from that style shows up as a missing variable, not as a silent pass.
/// </para>
/// <para>
/// The <c>__</c> → <c>:</c> step below is what <c>AddEnvironmentVariables()</c> does at runtime; it is
/// applied here rather than by setting real process environment variables, which would be shared
/// mutable state across a parallel test run. What this test guards is the <em>names</em>, not that
/// separator.
/// </para>
/// </remarks>
public class GatewayInfraBindingTests
{
    private const string Section = "Gateway";

    /// <summary>How many items a scraped collection setting is given, so ordering is provable.</summary>
    private const int CollectionSize = 2;

    /// <summary>
    /// The three shapes a <c>Gateway</c> setting takes in the bicep:
    /// <c>Gateway__Member</c> (scalar), <c>Gateway__Member__${i}</c> (list of scalars, e.g.
    /// <c>FoundryAccountNames</c>), and <c>Gateway__Member__${i}__Field</c> (list of objects — the form
    /// <c>Gateway__ModelAliases__${i}__Tier</c> (#211) and <c>Gateway__Tiers__${i}__ProductId</c>
    /// (#201) take).
    /// </summary>
    private static readonly Regex GatewaySettingRegex = new(
        @"name:\s*'Gateway__(?<member>[A-Za-z0-9]+)(?<collection>__\$\{i\})?(?:__(?<field>[A-Za-z0-9]+))?'",
        RegexOptions.Compiled);

    /// <summary>
    /// Members of <see cref="GatewayOptions"/> that infra deliberately does <b>not</b> set, and why.
    /// Anything not listed here must appear in the bicep — that is the whole point of the section.
    /// Empty since #201 turned the tier table, the last holdout, into an infra-emitted setting.
    /// </summary>
    private static readonly Dictionary<string, string> NotSetByInfra = new(StringComparer.Ordinal);

    [Fact]
    public void Every_Gateway_variable_infra_sets_binds_to_a_GatewayOptions_member()
    {
        var settings = ScrapeGatewaySettings(ControlPlaneBicep());
        Assert.NotEmpty(settings);

        BindAndVerify(settings);
    }

    /// <summary>
    /// The same machinery over a hand-written snippet rather than the real bicep, so the alarm's grasp
    /// of the list-of-objects shape is pinned independently of what any bicep happens to emit today
    /// (#204 review). Bound against <see cref="GatewayOptions.Tiers"/>, so it is a real end-to-end
    /// proof that a nested-field key reaches its property rather than only that the regex matches it —
    /// which is what let #201 be written knowing the shape would bind.
    /// </summary>
    [Fact]
    public void A_nested_field_setting_binds_to_the_list_items_property()
    {
        var settings = ScrapeGatewaySettings(
            """
              { name: 'Gateway__Tiers__${i}__ProductId', value: tier.name }
              { name: 'Gateway__Tiers__${i}__DisplayName', value: tier.displayName }
              { name: 'Gateway__Tiers__${i}__MonthlyTokenQuota', value: string(tier.monthlyTokenQuota) }
            """);

        var setting = Assert.Single(settings);
        Assert.Equal(nameof(GatewayOptions.Tiers), setting.Member);
        Assert.True(setting.IsCollection);
        Assert.Equal([nameof(GatewayTier.ProductId), nameof(GatewayTier.DisplayName), nameof(GatewayTier.MonthlyTokenQuota)], setting.Fields);

        var options = BindAndVerify(settings);
        Assert.Equal(CollectionSize, options.Tiers.Count);
    }

    /// <summary>
    /// The alarm's own claim, tested. The configuration binder ignores keys it cannot place, so the
    /// failure mode this guards against is entirely silent: without these two cases green, the checks
    /// above would pass on a bicep that sets a variable nothing reads (#204 review).
    /// </summary>
    [Theory]
    [InlineData("Gateway__Whatever", "no settable `Whatever` property")]
    [InlineData("Gateway__ApimName__${i}", "an indexed list")]
    [InlineData("Gateway__Tiers__${i}__NoSuchField", "no settable `NoSuchField` property")]
    public void The_binding_check_fails_when_infra_sets_something_nothing_binds(string name, string expectedInMessage)
    {
        var settings = ScrapeGatewaySettings($"  {{ name: '{name}', value: whatever }}");

        var failure = Assert.ThrowsAny<Exception>(() => BindAndVerify(settings));

        Assert.Contains(expectedInMessage, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Gateway__SubscriptionId", "SubscriptionId", false, "")]
    [InlineData("Gateway__FoundryAccountNames__${i}", "FoundryAccountNames", true, "")]
    [InlineData("Gateway__ModelAliases__${i}__Tier", "ModelAliases", true, "Tier")]
    [InlineData("Gateway__Tiers__${i}__MonthlyTokenQuota", "Tiers", true, "MonthlyTokenQuota")]
    public void The_scrape_understands_every_shape_a_Gateway_setting_takes(string name, string member, bool isCollection, string field)
    {
        ArgumentNullException.ThrowIfNull(field);

        // Pinned separately from the binding so a regex that silently stops matching one shape fails
        // with "the scrape missed this", not with a downstream "member unaccounted for" further away
        // from the cause. ModelAliases has no property yet (#211); parsing must still work.
        var setting = Assert.Single(ScrapeGatewaySettings($"  {{ name: '{name}', value: whatever }}"));

        Assert.Equal(member, setting.Member);
        Assert.Equal(isCollection, setting.IsCollection);
        Assert.Equal(field.Length == 0 ? [] : new[] { field }, setting.Fields);
    }

    [Fact]
    public void Every_GatewayOptions_member_is_either_set_by_infra_or_listed_as_deliberately_absent()
    {
        var fromInfra = ScrapeGatewaySettings(ControlPlaneBicep())
            .Select(setting => setting.Member)
            .ToHashSet(StringComparer.Ordinal);

        var unaccounted = BindableMembers()
            .Select(property => property.Name)
            .Where(name => !fromInfra.Contains(name) && !NotSetByInfra.ContainsKey(name))
            .ToList();

        Assert.True(
            unaccounted.Count == 0,
            $"GatewayOptions member(s) {string.Join(", ", unaccounted)} are neither set by " +
            "infra/modules/control-plane.bicep nor listed in NotSetByInfra. Every member of the Gateway " +
            "section has to come from somewhere on a deployed host: either add a `Gateway__{name}` line to " +
            "the bicep's sharedAppConfig (and to reference/configuration.mdx), or add it to NotSetByInfra " +
            "with the reason it does not need one. Silently binding nothing is how a control plane ends up " +
            "addressing a gateway it cannot reach (#108).");
    }

    [Fact]
    public void Infra_emits_the_tier_table_with_every_field_a_GatewayTier_binds()
    {
        // The inverse of what this test asserted before #201, and the reason the issue existed: a
        // developer's budget IS a tier (D-013), so a fork that overrode `quotaTiers` at deploy time got
        // APIM products and llm-token-limit caps the control plane had never heard of. Both now come
        // from the one parameter. The field list is pinned as well as the presence: an infra that
        // emitted only ProductId would bind a table of nameless zero-cap (= unlimited) tiers, which
        // validates and is wrong.
        var tiers = Assert.Single(
            ScrapeGatewaySettings(ControlPlaneBicep()),
            setting => setting.Member == nameof(GatewayOptions.Tiers));

        Assert.True(tiers.IsCollection);
        Assert.Equal(
            [nameof(GatewayTier.ProductId), nameof(GatewayTier.DisplayName), nameof(GatewayTier.MonthlyTokenQuota)],
            tiers.Fields);
    }

    [Fact]
    public void The_tier_table_infra_emits_is_derived_from_the_quotaTiers_parameter()
    {
        // Names, not values: the bicep's `quotaTiers` default is checked against the local tier table by
        // GatewayOptionsTiersTests and against GatewayTiers by GatewayTiersTests. What only this test
        // can see is that the settings are *derived from the parameter* rather than typed out again — a
        // second literal copy in control-plane.bicep would pass every other check in the repository
        // while a fork's override silently failed to reach the control plane, which is the whole of
        // #201.
        var bicep = WithoutComments(ControlPlaneBicep());

        Assert.Contains("param quotaTiers array", bicep, StringComparison.Ordinal);
        Assert.Contains("map(quotaTiers, tier =>", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_hosts_get_the_same_gateway_configuration()
    {
        // The Api reads the addressing, the Functions host reads the workspace GUID, and both bind the
        // whole section — so a Gateway variable added for one host must reach the other or the two
        // disagree about the same gateway. Comments are stripped first: a `//` line naming the two
        // identifiers would otherwise satisfy this without either host being passed anything (#204 review).
        foreach (var module in new[] { "containerApp", "functionApp" })
        {
            var declaration = WithoutComments(ModuleDeclaration(module));
            Assert.Contains("sharedAppConfig", declaration, StringComparison.Ordinal);
            Assert.Contains("foundryAccountConfig", declaration, StringComparison.Ordinal);

            // Every block the hosts are handed, not just the two that existed when this test was
            // written — otherwise a later change that wires a new block to one host slips through
            // (#211 review). modelAliasConfig is the alias map (#153); quotaTierConfig is the quota
            // tier table (#201), and the Functions host needs it as much as the Api does — quota
            // resolution runs in the monthly reset.
            Assert.Contains("modelAliasConfig", declaration, StringComparison.Ordinal);
            Assert.Contains("quotaTierConfig", declaration, StringComparison.Ordinal);
        }
    }

    // -- The machinery --

    /// <summary>One <c>Gateway</c> member as the bicep sets it: scalar, list of scalars, or list of objects with these fields.</summary>
    private sealed record GatewaySetting(string Member, bool IsCollection, IReadOnlyList<string> Fields);

    /// <summary>
    /// Every distinct <c>Gateway__*</c> member in <paramref name="bicep"/>, with the shape its keys
    /// imply. A member is a list of objects when its keys carry a field after the index, a list of
    /// scalars when they carry the index alone, and a scalar otherwise.
    /// </summary>
    private static List<GatewaySetting> ScrapeGatewaySettings(string bicep)
    {
        var byMember = new Dictionary<string, (bool IsCollection, List<string> Fields)>(StringComparer.Ordinal);

        foreach (Match match in GatewaySettingRegex.Matches(bicep))
        {
            var member = match.Groups["member"].Value;
            var isCollection = match.Groups["collection"].Success;
            var field = match.Groups["field"].Success ? match.Groups["field"].Value : null;

            Assert.True(
                field is null || isCollection,
                $"Gateway__{member}__{field} names a nested field with no `__${{i}}` index between them. " +
                "That is an object-valued member, a shape this test does not model — teach ScrapeGatewaySettings " +
                "about it in the same change that adds it, rather than letting it bind unchecked.");

            if (!byMember.TryGetValue(member, out var shape))
            {
                shape = (isCollection, []);
                byMember[member] = shape;
            }

            Assert.Equal(shape.IsCollection, isCollection);
            if (field is not null && !shape.Fields.Contains(field, StringComparer.Ordinal))
            {
                shape.Fields.Add(field);
            }
        }

        return [.. byMember
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new GatewaySetting(pair.Key, pair.Value.IsCollection, pair.Value.Fields))];
    }

    /// <summary>
    /// Turns <paramref name="settings"/> into a configuration source with a distinct generated value per
    /// key, binds it, and asserts by reflection that every member came back carrying exactly those
    /// values. Distinct values matter: a member bound from the wrong key is then a value mismatch rather
    /// than an accidental pass.
    /// </summary>
    private static GatewayOptions BindAndVerify(IReadOnlyList<GatewaySetting> settings)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var setting in settings)
        {
            var property = PropertyFor(setting);

            if (!setting.IsCollection)
            {
                values[$"{Section}:{setting.Member}"] = Generated(property.PropertyType, setting.Member, index: 0);
                continue;
            }

            var elementType = ElementType(property, setting);
            for (var index = 0; index < CollectionSize; index++)
            {
                if (setting.Fields.Count == 0)
                {
                    values[$"{Section}:{setting.Member}:{index}"] = Generated(elementType, setting.Member, index);
                    continue;
                }

                foreach (var field in setting.Fields)
                {
                    var fieldProperty = FieldPropertyFor(setting, elementType, field);
                    values[$"{Section}:{setting.Member}:{index}:{field}"] = Generated(fieldProperty.PropertyType, $"{setting.Member}.{field}", index);
                }
            }
        }

        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build()
            .GetSection(Section)
            .Get<GatewayOptions>();

        Assert.NotNull(options);

        foreach (var setting in settings)
        {
            var property = PropertyFor(setting);
            var bound = property.GetValue(options);

            if (!setting.IsCollection)
            {
                Assert.Equal(
                    Expected(property.PropertyType, Generated(property.PropertyType, setting.Member, index: 0)),
                    bound);
                continue;
            }

            var items = Assert.IsAssignableFrom<IList>(bound);
            Assert.True(
                items.Count == CollectionSize,
                $"infra sets Gateway__{setting.Member}__${{i}} but only {items.Count} of {CollectionSize} items reached " +
                $"GatewayOptions.{setting.Member}. The binder ignores keys it cannot place, so this is a name or shape mismatch.");

            var elementType = ElementType(property, setting);
            for (var index = 0; index < CollectionSize; index++)
            {
                if (setting.Fields.Count == 0)
                {
                    Assert.Equal(Generated(elementType, setting.Member, index), items[index]);
                    continue;
                }

                foreach (var field in setting.Fields)
                {
                    var fieldProperty = FieldPropertyFor(setting, elementType, field);
                    Assert.Equal(
                        Expected(fieldProperty.PropertyType, Generated(fieldProperty.PropertyType, $"{setting.Member}.{field}", index)),
                        fieldProperty.GetValue(items[index]));
                }
            }
        }

        return options;
    }

    private static PropertyInfo PropertyFor(GatewaySetting setting)
    {
        var property = BindableMembers().FirstOrDefault(candidate => candidate.Name == setting.Member);

        Assert.True(
            property is not null,
            $"infra/modules/control-plane.bicep sets Gateway__{setting.Member}… but GatewayOptions has no settable " +
            $"`{setting.Member}` property, so the configuration binder drops it silently and the control plane never " +
            "sees the value. Add the property (and document it in reference/configuration.mdx), or remove the bicep line (#108).");

        return property!;
    }

    private static Type ElementType(PropertyInfo property, GatewaySetting setting)
    {
        Assert.True(
            property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(List<>),
            $"infra sets Gateway__{setting.Member}__${{i}} — an indexed list — but GatewayOptions.{setting.Member} " +
            $"is {property.PropertyType.Name}, which the binder cannot fill from indexed keys.");

        return property.PropertyType.GetGenericArguments()[0];
    }

    private static PropertyInfo FieldPropertyFor(GatewaySetting setting, Type elementType, string field)
    {
        var property = elementType.GetProperty(field, BindingFlags.Public | BindingFlags.Instance);

        Assert.True(
            property is not null && property.CanWrite,
            $"infra sets Gateway__{setting.Member}__${{i}}__{field} but {elementType.Name} has no settable `{field}` " +
            "property, so that field of every item binds to nothing (#108).");

        return property!;
    }

    /// <summary>
    /// <see cref="Generated"/>'s string turned back into the value the binder should have produced.
    /// <c>Convert.ChangeType</c> cannot do enums, so those are parsed by name.
    /// </summary>
    private static object Expected(Type type, string generated)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        return target.IsEnum
            ? Enum.Parse(target, generated)
            : Convert.ChangeType(generated, target, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A distinct, type-appropriate value for one key — a sentinel naming the member for strings, a
    /// deterministic number or flag for the primitives a tier table uses, and a real member name for an
    /// enum.
    /// </summary>
    private static string Generated(Type type, string label, int index)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(string))
        {
            return $"{label}#{index}";
        }

        if (target.IsEnum)
        {
            // A sentinel string does not parse as an enum, so the binder would silently leave the
            // property at its default and this test would pass while proving nothing. Rotating through
            // the declared names keeps the value distinct per index and still round-trips — which is
            // what makes Gateway__ModelAliases__{i}__Provider actually verified (#153/#211).
            var names = Enum.GetNames(target);
            return names[index % names.Length];
        }

        if (target == typeof(bool))
        {
            return index % 2 == 0 ? "true" : "false";
        }

        if (target == typeof(long) || target == typeof(int) || target == typeof(short))
        {
            // Deterministic and distinct per (label, index); stays inside int range for narrower types.
            var seed = Math.Abs(label.Aggregate(7, (accumulated, character) => (accumulated * 31) + character) % 1_000) + 1;
            return ((seed * 1_000) + index).ToString(CultureInfo.InvariantCulture);
        }

        Assert.Fail(
            $"No generated value for {target.Name} (Gateway:{label}). Teach GatewayInfraBindingTests.Generated about " +
            "the type in the same change that adds the setting, so the binding is still actually verified.");
        return string.Empty;
    }

    private static IEnumerable<PropertyInfo> BindableMembers() =>
        typeof(GatewayOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite);

    /// <summary>
    /// The text of one <c>module &lt;name&gt; '…' { … }</c> declaration: from its <c>module</c> keyword to
    /// the next top-level <c>module</c>/<c>output</c>, which is all this needs and avoids matching braces
    /// with a regex.
    /// </summary>
    private static string ModuleDeclaration(string moduleName)
    {
        var bicep = ControlPlaneBicep().Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = bicep.IndexOf($"\nmodule {moduleName} '", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find `module {moduleName} '…'` in infra/modules/control-plane.bicep.");

        var next = new[] { bicep.IndexOf("\nmodule ", start + 1, StringComparison.Ordinal), bicep.IndexOf("\noutput ", start + 1, StringComparison.Ordinal) }
            .Where(index => index >= 0)
            .DefaultIfEmpty(bicep.Length)
            .Min();

        return bicep[start..next];
    }

    /// <summary>Drops <c>//</c> line comments, so prose mentioning an identifier cannot stand in for code using it.</summary>
    private static string WithoutComments(string bicep) =>
        Regex.Replace(bicep, @"//[^\n]*", string.Empty);

    private static string ControlPlaneBicep() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "modules", "control-plane.bicep"));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FoundryGate.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (FoundryGate.sln) from the test output directory.");
    }
}
