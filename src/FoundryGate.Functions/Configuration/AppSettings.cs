using System.ComponentModel.DataAnnotations;
using FoundryGate.Core.Configuration;

namespace FoundryGate.Functions.Configuration;

/// <summary>
/// Root options object for FoundryGate.Functions (CONVENTIONS.md §Configuration &amp; auth: "one
/// <c>Configuration/AppSettings.cs</c> per host, nested option classes with DataAnnotations,
/// <c>ValidateRecursively()</c> at startup"). Bound once in <c>Program.cs</c> after
/// <c>@KeyVault()</c> reference resolution, then validated — a Function App that cannot reach its
/// database or has no tier table must fail at startup, not fifteen minutes later inside a timer.
/// </summary>
/// <remarks>
/// Deliberately a fraction of the Api's: no <c>AzureAd</c> (nothing here serves requests, so there
/// is no token to validate), no <c>Cors</c>, no <c>KeyProtection</c> (these jobs never read or write
/// a developer's key). Everything it does have, <c>infra/modules/control-plane.bicep</c> already sets
/// on the Function App — see <c>docs-site reference/configuration</c>.
/// </remarks>
public class AppSettings : IValidatableObject
{
    /// <summary>Binds the standard ASP.NET Core <c>ConnectionStrings</c> section (<c>ConnectionStrings__FoundryGate</c>, Entra auth — no password).</summary>
    [Required]
    public ConnectionStringOptions ConnectionStrings { get; set; } = new();

    /// <summary>
    /// The same <c>Gateway</c> section the Api binds (#108/#119): the quota <see cref="GatewayOptions.Tiers"/>
    /// resolution needs, and <see cref="GatewayOptions.LogAnalyticsWorkspaceId"/> the reconciliation job
    /// queries. The ARM addressing members are unused here — these jobs never call the APIM management
    /// plane — but the section binds whole rather than in a Functions-specific subset that would drift.
    /// </summary>
    [Required]
    public GatewayOptions Gateway { get; set; } = new();

    /// <summary>
    /// The same <c>Entra</c> section the Api binds (#151): Microsoft Graph directory access for the
    /// nightly <c>EntraSyncFunction</c>, which runs the users sync and then the group sync. Off by
    /// default (<c>Entra:Enabled</c>), in which case the job logs once and does nothing — a fork that
    /// has not granted the Graph application roles must not see a failed invocation every night.
    /// </summary>
    public EntraOptions Entra { get; set; } = new();

    /// <summary>Where the scheduled jobs' distributed locks live. Optional: absent, they fall back to a no-op lock (see <see cref="StorageOptions"/>).</summary>
    public StorageOptions Storage { get; set; } = new();

    /// <summary>OpenTelemetry → Azure Monitor for the worker. Off by default so `func start` needs no connection string.</summary>
    public OpenTelemetryOptions OpenTelemetry { get; set; } = new();

    /// <summary>Azure Key Vault reference resolution (<c>Azure__KeyVaultUrl</c>). Optional, exactly as in the Api.</summary>
    public AzureOptions Azure { get; set; } = new();

    /// <summary>
    /// The rules of the two Core-owned sections — <c>ValidateRecursively()</c> only recurses into
    /// property types from this assembly, and both <see cref="GatewayOptions"/> and
    /// <see cref="EntraOptions"/> live in <c>FoundryGate.Core</c> (#119/#151;
    /// CONVENTIONS.md §Solution structure) — plus the one Entra rule that belongs to <em>this</em>
    /// host: with the directory enabled, something has to say which application's assignments define
    /// the user population. The Api defaults that from <c>AzureAd:ClientId</c> when it builds the
    /// directory client; nothing here serves a request, so there is no <c>AzureAd</c> section to fall
    /// back to and the value has to be deployed (infra sets <c>Entra__ApplicationClientId</c>).
    /// Catching it at startup beats finding out at 02:00 inside a timer trigger.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in CoreOptionsValidation.ValidateGateway(Gateway, nameof(Gateway)))
        {
            yield return result;
        }

        foreach (var result in CoreOptionsValidation.ValidateEntra(Entra, nameof(Entra)))
        {
            yield return result;
        }

        if (Entra.Enabled
            && string.IsNullOrWhiteSpace(Entra.ApplicationClientId)
            && string.IsNullOrWhiteSpace(Entra.ServicePrincipalObjectId))
        {
            yield return new ValidationResult(
                $"{nameof(Entra)}.{nameof(EntraOptions.ApplicationClientId)} is required when {nameof(Entra)}.{nameof(EntraOptions.Enabled)} is true and no " +
                $"{nameof(EntraOptions.ServicePrincipalObjectId)} is set: the FoundryGate service principal whose app-role assignments define the user " +
                "population is resolved from it. infra/modules/control-plane.bicep sets Entra__ApplicationClientId on both hosts.",
                [$"{nameof(Entra)}.{nameof(EntraOptions.ApplicationClientId)}"]);
        }
    }
}

/// <summary>Binds the standard ASP.NET Core <c>ConnectionStrings</c> configuration section.</summary>
public class ConnectionStringOptions
{
    /// <summary>SQL Server connection string for <see cref="Data.AppDbContext"/>.</summary>
    [Required]
    public string FoundryGate { get; set; } = string.Empty;
}

/// <summary>
/// The Functions storage account, used for one thing beyond the host's own runtime state: the blob
/// lease that stops two replicas running the monthly reset at once (#38).
/// </summary>
/// <remarks>
/// <para>
/// Both address members are optional and both normally stay empty, because the host is already told
/// where its storage account is: <c>infra/modules/function-app.bicep</c> sets
/// <c>AzureWebJobsStorage__accountName</c> (identity-based; shared-key access is disabled on the
/// account), and `func start` locally sets <c>AzureWebJobsStorage</c> to
/// <c>UseDevelopmentStorage=true</c>. <c>Program.cs</c> falls back to those, so no new infra
/// environment variable was needed for this feature.
/// </para>
/// <para>
/// With neither configured nor discoverable — a bare unit-test host, or a `func start` without
/// Azurite — the reset registers a lock that always grants and says so at Warning. Correctness does
/// not depend on it: the reset is idempotent, and a Timer trigger is already singleton across
/// instances; the lease is the second belt.
/// </para>
/// </remarks>
public class StorageOptions : IValidatableObject
{
    /// <summary>Storage account name (identity-based access). Overrides the host's <c>AzureWebJobsStorage__accountName</c> when set.</summary>
    public string? AccountName { get; set; }

    /// <summary>Storage connection string — local Azurite (<c>UseDevelopmentStorage=true</c>) only; never a cloud shared key (CONVENTIONS.md §Storage accounts).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Blob container holding the lock blobs. Created on demand if missing.</summary>
    [Required]
    [StringLength(63, MinimumLength = 3)]
    public string LockContainerName { get; set; } = "foundrygate-locks";

    /// <summary>How long the reset's lease is held before Azure breaks it — long enough for a full reset, short enough that a crashed replica does not block the next day's run.</summary>
    [Range(15, 60)]
    public int LockLeaseSeconds { get; set; } = 60;

    /// <summary>
    /// Fail-fast: a cloud storage connection string would be a shared key, which the account does not
    /// even accept (<c>allowSharedKeyAccess: false</c>) — catching it here beats a 403 from a timer.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString)
            && !ConnectionString.Contains("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ValidationResult(
                $"{nameof(ConnectionString)} is for local Azurite only (UseDevelopmentStorage=true). In Azure, set {nameof(AccountName)} — or leave both empty and let the host's AzureWebJobsStorage__accountName decide — so the lease uses the managed identity.",
                [nameof(ConnectionString)]);
        }
    }
}

/// <summary>OpenTelemetry → Azure Monitor for the isolated worker, gated by <see cref="Enabled"/>.</summary>
public class OpenTelemetryOptions : IValidatableObject
{
    /// <summary>Off by default so a local `func start` needs no Application Insights connection string. Infra sets <c>OpenTelemetry__Enabled=true</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Application Insights connection string. Required when <see cref="Enabled"/>, so telemetry is never silently dropped.</summary>
    public string? ConnectionString { get; set; }

    /// <inheritdoc cref="OpenTelemetryOptions"/>
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
    /// <summary>Optional. Absent (the local default) skips <c>@KeyVault(SecretName)</c> resolution entirely, so nothing here needs Azure connectivity to start.</summary>
    public string? KeyVaultUrl { get; set; }
}
