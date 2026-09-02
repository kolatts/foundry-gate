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
public class AppSettings
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
    /// quota <see cref="GatewayOptions.Tiers"/> (<c>Gateway:Tiers</c>, shipped in <c>appsettings.json</c>;
    /// issue #32 / D-013). The type lives in <c>FoundryGate.Core</c> because the Functions host binds
    /// the same section (#119). See <see cref="GatewayOptions"/>.</summary>
    [Required]
    public GatewayOptions Gateway { get; set; } = new();

    /// <summary>Microsoft Graph directory sync (#40/#41). Off by default so local dev and the test
    /// host never need Graph connectivity or Graph application roles.</summary>
    public EntraOptions Entra { get; set; } = new();

    /// <summary>How <c>User.ApimSubscriptionKey</c> is encrypted at rest (#95).</summary>
    public KeyProtectionOptions KeyProtection { get; set; } = new();
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
