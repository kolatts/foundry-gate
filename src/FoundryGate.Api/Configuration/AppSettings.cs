using System.ComponentModel.DataAnnotations;
using FoundryGate.Core.Configuration;

namespace FoundryGate.Api.Configuration;

/// <summary>
/// Root options object for FoundryGate.Api (CONVENTIONS.md §Configuration &amp; auth: "Options
/// pattern, fail-fast: one <c>Configuration/AppSettings.cs</c> per host, nested option classes
/// with DataAnnotations, <c>ValidateRecursively()</c> at startup"). Bound once in
/// <c>Program.cs</c> via <c>IConfiguration.Get&lt;AppSettings&gt;()</c> — after
/// <c>@KeyVault()</c> reference resolution — then validated with
/// <see cref="Imagile.Framework.Configuration.Extensions.ConfigurationValidationExtensions.ValidateRecursively"/>,
/// which throws <see cref="Imagile.Framework.Configuration.Exceptions.ConfigurationValidationException"/>
/// with every violation aggregated into one message rather than failing on the first.
/// </summary>
public class AppSettings : IValidatableObject
{
    /// <summary>Entra ID bearer-token validation (spec §4, §11). Always required — every
    /// <c>/api/v1</c> endpoint needs a valid token, in every environment including local.</summary>
    [Required]
    public AzureAdOptions AzureAd { get; set; } = new();

    /// <summary>Binds the standard ASP.NET Core <c>ConnectionStrings</c> section.</summary>
    [Required]
    public ConnectionStringOptions ConnectionStrings { get; set; } = new();

    /// <summary>Browser origins allowed to call <c>/api/v1</c> (the Blazor WASM UI's origin).</summary>
    public CorsOptions Cors { get; set; } = new();

    /// <summary>OpenTelemetry → Azure Monitor. Off by default so local dev never needs an
    /// Application Insights connection string.</summary>
    public OpenTelemetryOptions OpenTelemetry { get; set; } = new();

    /// <summary>Azure Key Vault reference resolution. Optional: absent
    /// <see cref="AzureOptions.KeyVaultUrl"/> skips resolution entirely so local dev runs
    /// with docker SQL and no Azure connectivity at all.</summary>
    public AzureOptions Azure { get; set; } = new();

    /// <summary>Gateway data-plane addressing set by infra (<c>Gateway__*</c>, issue #108 — optional as
    /// a whole; absent locally; <c>/foundry/*</c> and <c>/keys/*</c> need it) plus the always-required
    /// quota <see cref="GatewayOptions.Tiers"/> (<c>Gateway__Tiers__{i}__*</c>, also set by infra — from
    /// the same <c>quotaTiers</c> parameter that creates the APIM products, #201 — and locally from
    /// <c>appsettings.local.json</c>; issue #32 / D-013). The type lives in <c>FoundryGate.Core</c> because the Functions host binds
    /// the same section (#119). See <see cref="GatewayOptions"/>.</summary>
    [Required]
    public GatewayOptions Gateway { get; set; } = new();

    /// <summary>Microsoft Graph directory sync (#40/#41). Off by default so local dev and the test
    /// host never need Graph connectivity or Graph application roles.</summary>
    public EntraOptions Entra { get; set; } = new();

    /// <summary>How <c>User.ApimSubscriptionKey</c> is encrypted at rest (#95).</summary>
    public KeyProtectionOptions KeyProtection { get; set; } = new();

    /// <summary>
    /// Limits and signals protecting the two routes that hand a developer their own gateway
    /// credential (#180/#181). Every member defaults to the value that shipped as a constant, so a
    /// fork that configures nothing behaves exactly as it did.
    /// </summary>
    public SecurityOptions Security { get; set; } = new();

    /// <summary>
    /// Validates the sections whose option classes live in <c>FoundryGate.Core</c>:
    /// <c>ValidateRecursively()</c> only recurses into types from the root object's own assembly, so
    /// <see cref="Gateway"/> has to be handed to it explicitly (see
    /// <see cref="CoreOptionsValidation"/>). Everything declared in this assembly is still walked
    /// automatically.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        CoreOptionsValidation.ValidateGateway(Gateway, nameof(Gateway));
}

/// <summary>
/// The <c>Security</c> section: what protects <c>POST /keys/me/reveal</c> and
/// <c>POST /keys/me/rotate</c>, the two routes a leaked bearer token can replay for a developer's
/// plaintext gateway credential.
/// </summary>
/// <remarks>
/// These were <c>private const</c>s in <c>RateLimiterExtensions</c> until #181. Configuration buys
/// two things: a fork whose developers work differently can retune without recompiling, and the
/// endpoint tests can shorten the window instead of racing a real 60-second one —
/// <c>System.Threading.RateLimiting</c>'s limiters own their replenishment timer and take no
/// <c>TimeProvider</c>, so <c>ApiTestFactory.TimeProvider</c> cannot move them.
/// </remarks>
public class SecurityOptions
{
    /// <summary>Per-caller limits on the <c>/keys/me</c> routes (#136/#181).</summary>
    [Required]
    public KeyRateLimitOptions RateLimits { get; set; } = new();

    /// <summary>The reveal anomaly signal (#180) — the patient drain a limiter cannot see.</summary>
    [Required]
    public RevealAnomalyOptions RevealAnomaly { get; set; } = new();
}

/// <summary>The two fixed-window policies <c>RateLimiterExtensions</c> registers (<c>Security:RateLimits</c>).</summary>
public class KeyRateLimitOptions
{
    /// <summary>
    /// <c>POST /keys/me/reveal</c>. The UI reveals at most once per page load; the default five leaves
    /// room for a developer flipping between tabs and still cuts a scripted drain down to a rate an
    /// audit review would catch.
    /// </summary>
    [Required]
    public RateLimitPolicyOptions Reveal { get; set; } = new() { PermitLimit = 5 };

    /// <summary>
    /// <c>POST /keys/me/rotate</c>. Lower than reveal because rotation is a write the gateway feels:
    /// each call regenerates both APIM keys and breaks whatever the developer has configured, so
    /// nobody legitimately needs a fourth in a minute.
    /// </summary>
    [Required]
    public RateLimitPolicyOptions Rotate { get; set; } = new() { PermitLimit = 3 };
}

/// <summary>One fixed-window policy: how many requests a caller gets, and how long the window is.</summary>
public class RateLimitPolicyOptions
{
    /// <summary>Requests allowed per <see cref="WindowSeconds"/> per caller <c>oid</c>.</summary>
    [Range(1, 10_000)]
    public int PermitLimit { get; set; }

    /// <summary>
    /// Length of the fixed window. One minute by default — the window <c>Retry-After</c> reports, so a
    /// value a client would find absurd to wait out is a configuration mistake worth failing on.
    /// </summary>
    [Range(1, 3_600)]
    public int WindowSeconds { get; set; } = 60;

    /// <summary><see cref="WindowSeconds"/> as a <see cref="TimeSpan"/>, for the limiter options.</summary>
    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}

/// <summary>
/// The reveal anomaly signal (<c>Security:RevealAnomaly</c>, #180): a rate limiter stops a <em>fast</em>
/// drain and does nothing about a patient one — four reveals a minute, every minute, is well inside
/// the cap and is not something a human does.
/// </summary>
public class RevealAnomalyOptions
{
    /// <summary>
    /// How many <c>key.revealed</c> rows one user may accumulate inside <see cref="WindowMinutes"/>
    /// before the reveal that crosses the line logs a Warning and writes a
    /// <c>key.reveal-anomaly</c> row. Ten an hour is roughly twice the busiest plausible human day
    /// compressed into one hour.
    /// </summary>
    [Range(1, 10_000)]
    public int Threshold { get; set; } = 10;

    /// <summary>The rolling window the rows are counted over. Long enough that a slow drain still adds up; short enough that last week's incident does not keep firing.</summary>
    [Range(1, 10_080)]
    public int WindowMinutes { get; set; } = 60;

    /// <summary>
    /// Set <see cref="Threshold"/> unreachably high rather than turning this off; there is no
    /// <c>Enabled</c> flag because the check is a single grouped <c>COUNT</c> served by
    /// <c>AuditLog</c>'s <c>(Action, TargetType, TargetId, OccurredDate)</c> index, on a route that
    /// already writes a row and decrypts a key.
    /// </summary>
    public TimeSpan Window => TimeSpan.FromMinutes(WindowMinutes);
}

/// <summary>Which <c>IKeyProtector</c> encrypts <c>User.ApimSubscriptionKey</c> at rest (#95).</summary>
public enum KeyProtectionProviderType
{
    /// <summary>
    /// Azure Key Vault RSA-OAEP-256 key wrapping with <see cref="GatewayOptions.KeyEncryptionKeyUri"/>
    /// through the app's managed identity (Key Vault Crypto User). The only provider allowed outside
    /// <c>local</c>.
    /// </summary>
    KeyVault,

    /// <summary>
    /// ASP.NET Core Data Protection with the machine-local key ring — no Azure needed, so local dev
    /// and the integration tests run hermetically. Refused at startup in <c>qa</c>/<c>prod</c>.
    /// </summary>
    DataProtection,
}

/// <summary>Bound from the <c>KeyProtection</c> section.</summary>
public class KeyProtectionOptions
{
    /// <summary>
    /// Defaults to <see cref="KeyProtectionProviderType.KeyVault"/> so a cloud environment that
    /// forgets the section fails closed (missing key URI → startup error) rather than silently
    /// falling back to a machine-local key ring; <c>appsettings.local.json</c> opts into
    /// <see cref="KeyProtectionProviderType.DataProtection"/>. (No <c>[Required]</c>: it is a no-op
    /// on a non-nullable enum; the binder rejects unknown names and the default is the safe one.)
    /// </summary>
    public KeyProtectionProviderType Provider { get; set; } = KeyProtectionProviderType.KeyVault;
}

/// <summary>
/// Entra ID app registration settings bound from the <c>AzureAd</c> configuration section.
/// The same section is also read directly by <c>Microsoft.Identity.Web</c>'s
/// <c>AddMicrosoftIdentityWebApiAuthentication</c> in <c>Program.cs</c> — this class exists
/// purely so the values participate in fail-fast <c>ValidateRecursively()</c> at startup.
/// </summary>
public class AzureAdOptions
{
    /// <summary>Entra ID cloud instance. Defaults to the public cloud; forks in sovereign
    /// clouds override this.</summary>
    [Required]
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>The Entra ID tenant directory ID (GUID).</summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Client ID of the FoundryGate.Api app registration.</summary>
    [Required]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Expected token audience — normally <c>api://{ClientId}</c>.</summary>
    [Required]
    public string Audience { get; set; } = string.Empty;
}

/// <summary>Binds the standard ASP.NET Core <c>ConnectionStrings</c> configuration section.</summary>
public class ConnectionStringOptions
{
    /// <summary>SQL Server connection string for <see cref="Data.AppDbContext"/>.</summary>
    [Required]
    public string FoundryGate { get; set; } = string.Empty;
}

/// <summary>CORS origins allowed to call the API's <c>/api/v1</c> controllers.</summary>
public class CorsOptions
{
    /// <summary>Browser origins allowed to call the API (the Blazor WASM UI's origin —
    /// the Static Web App URL in cloud environments).</summary>
    public List<string> AllowedOrigins { get; set; } = [];
}

/// <summary>OpenTelemetry → Azure Monitor instrumentation, gated by <see cref="Enabled"/>.</summary>
public class OpenTelemetryOptions : IValidatableObject
{
    /// <summary>Off by default (appsettings.local.json keeps it off) so local dev never
    /// needs an Application Insights connection string. Turning this on without a
    /// <see cref="ConnectionString"/> fails startup — see <see cref="Validate"/> — rather than
    /// silently sending telemetry nowhere.</summary>
    public bool Enabled { get; set; }

    /// <summary>Azure Monitor Application Insights connection string. Not <c>[Required]</c>
    /// directly, since it's legitimately empty whenever <see cref="Enabled"/> is <c>false</c>;
    /// <see cref="Validate"/> enforces it conditionally instead.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Fail-fast: <see cref="Enabled"/> without a <see cref="ConnectionString"/> means
    /// telemetry would be silently dropped rather than shipped, which is worse than refusing
    /// to start.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Enabled && string.IsNullOrWhiteSpace(ConnectionString))
        {
            yield return new ValidationResult(
                $"{nameof(ConnectionString)} is required when {nameof(Enabled)} is true.",
                [nameof(ConnectionString)]);
        }
    }
}

/// <summary>Azure Key Vault reference resolution settings.</summary>
public class AzureOptions
{
    /// <summary>
    /// Optional. When absent (default for local dev), <c>@KeyVault(SecretName)</c> reference
    /// tokens in appsettings are left unresolved and resolution is skipped entirely — no
    /// <c>SecretClient</c> is constructed and no Azure connectivity is required to start the
    /// app (CONVENTIONS.md: "local dev must run with docker SQL and no Azure at all").
    /// </summary>
    public string? KeyVaultUrl { get; set; }
}
