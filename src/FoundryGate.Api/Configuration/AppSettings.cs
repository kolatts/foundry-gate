using System.ComponentModel.DataAnnotations;

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

    /// <summary>
    /// Where the gateway lives (#108): APIM addressing for subscription lifecycle calls and the
    /// Key Vault key that wraps stored APIM keys. Set by <c>infra/modules/control-plane.bicep</c> as
    /// <c>Gateway__*</c> environment variables; optional locally (no APIM → the key endpoints answer
    /// with a clear "not configured" error, everything else runs).
    /// </summary>
    public GatewayOptions Gateway { get; set; } = new();

    /// <summary>How <c>User.ApimSubscriptionKey</c> is encrypted at rest (#95).</summary>
    public KeyProtectionOptions KeyProtection { get; set; } = new();
}

/// <summary>
/// Gateway addressing bound from the <c>Gateway</c> section — the keys
/// <c>infra/modules/control-plane.bicep</c> sets on both hosts (#108), so nobody types ARM ids into
/// <c>SystemConfiguration</c> by hand. This wave binds the APIM and key-wrapping subset; the Foundry
/// (#61) and reconciliation (#84) waves add <c>ApimGatewayUrl</c>, <c>LogAnalyticsWorkspaceId</c> and
/// <c>FoundryAccountNames</c> to this same class.
/// </summary>
public class GatewayOptions : IValidatableObject
{
    /// <summary>Azure subscription id that holds the APIM instance (<c>Gateway__SubscriptionId</c>).</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>Resource group of the APIM instance (<c>Gateway__ResourceGroup</c>).</summary>
    public string? ResourceGroup { get; set; }

    /// <summary>APIM service name — the short name, not the ARM id (<c>Gateway__ApimName</c>).</summary>
    public string? ApimName { get; set; }

    /// <summary>
    /// Versionless Key Vault key URI (<c>https://{vault}.vault.azure.net/keys/fg-apim-key-encryption</c>)
    /// of the RSA key that wraps APIM subscription keys before they are stored (#95;
    /// <c>Gateway__KeyEncryptionKeyUri</c>). Versionless so a Key Vault key rotation needs no redeploy —
    /// each stored envelope records the exact key version that wrapped it. Required when
    /// <see cref="KeyProtectionOptions.Provider"/> is <see cref="KeyProtectionProviderType.KeyVault"/>.
    /// </summary>
    public string? KeyEncryptionKeyUri { get; set; }

    /// <summary><see langword="true"/> when all three APIM addressing values are present, i.e. the Api can reach the APIM management plane.</summary>
    public bool IsApimConfigured =>
        !string.IsNullOrWhiteSpace(SubscriptionId)
        && !string.IsNullOrWhiteSpace(ResourceGroup)
        && !string.IsNullOrWhiteSpace(ApimName);

    /// <summary>
    /// The three APIM values are all-or-nothing — a partially addressed APIM instance is a
    /// misconfiguration, not "APIM off" — and the key URI, when present, must be an absolute
    /// <c>https</c> URI (the shape <c>CryptographyClient</c> accepts).
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var anyApim = !string.IsNullOrWhiteSpace(SubscriptionId)
            || !string.IsNullOrWhiteSpace(ResourceGroup)
            || !string.IsNullOrWhiteSpace(ApimName);

        if (anyApim && !IsApimConfigured)
        {
            yield return new ValidationResult(
                $"{nameof(SubscriptionId)}, {nameof(ResourceGroup)} and {nameof(ApimName)} must all be set (or all be empty) to address the APIM instance.",
                [nameof(SubscriptionId), nameof(ResourceGroup), nameof(ApimName)]);
        }

        if (!string.IsNullOrWhiteSpace(KeyEncryptionKeyUri)
            && (!Uri.TryCreate(KeyEncryptionKeyUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult(
                $"{nameof(KeyEncryptionKeyUri)} must be an absolute https URI of a Key Vault key.",
                [nameof(KeyEncryptionKeyUri)]);
        }
    }
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
