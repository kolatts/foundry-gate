using FoundryGate.Api.Services.Foundry;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Data.Interceptors;
using FoundryGate.Data.Seeding;
using FoundryGate.Domain.Constants;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FoundryGate.Tests.Predeployment.Api;

/// <summary>
/// Integration-test host for FoundryGate.Api: the real <c>Program.cs</c> pipeline (auth filter,
/// exception handler, controllers, DI) with two substitutions — <see cref="AppDbContext"/> runs on a
/// single kept-open SQLite in-memory connection instead of SQL Server, and
/// <see cref="TestAuthHandler"/> replaces Entra bearer auth so requests can act as any identity via
/// headers — and every external-system client is a fake (<see cref="FoundryClient"/> for ARM).
/// Hermetic: no docker, no Azure. Reference data (<c>SystemConfiguration</c> defaults) is seeded on
/// startup exactly as a deploy would.
/// </summary>
/// <remarks>
/// Use as <c>IClassFixture&lt;ApiTestFactory&gt;</c>: one factory (one database) per test class,
/// shared by that class's tests — so seed rows with unique markers (oids, actions, target ids)
/// rather than asserting on absolute counts. <see cref="TimeProvider"/> is the app's clock; move it
/// to control anything time-dependent (quota periods, audit timestamps).
/// </remarks>
public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    private static readonly DateTimeOffset DefaultNow = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The host's primary Foundry account (<c>Gateway:FoundryAccountNames:0</c>).</summary>
    public const string PrimaryFoundryAccount = "fgtest-eus2";

    /// <summary>The host's pool-member Foundry account (<c>Gateway:FoundryAccountNames:1</c>).</summary>
    public const string SecondaryFoundryAccount = "fgtest-swc";

    private readonly SqliteConnection _connection;

    public ApiTestFactory()
    {
        // ":memory:" is per-connection, so every DbContext must share this one instance; keeping it
        // open for the factory's lifetime is what keeps the database alive between scopes.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    /// <summary>The host's <see cref="System.TimeProvider"/>: <c>2026-09-01T12:00:00Z</c> until a test moves it.</summary>
    public MutableTimeProvider TimeProvider { get; } = new(DefaultNow);

    /// <summary>
    /// The host's <see cref="IFoundryManagementClient"/>: an in-memory fake pre-loaded with the two
    /// configured (empty) accounts. Seed deployments on it and assert on its recorded calls; it is
    /// shared by the class's tests, so use unique deployment names.
    /// </summary>
    public FakeFoundryManagementClient FoundryClient { get; } = CreateFoundryClient();

    /// <summary>
    /// The in-memory APIM standing in for the management plane (<see cref="IApimManagementClient"/>):
    /// seed orphan subscriptions, read back keys, or assert on calls. One per factory, like the database.
    /// </summary>
    public FakeApimManagementClient Apim => Services.GetRequiredService<FakeApimManagementClient>();

    /// <summary>
    /// A client authenticated as <paramref name="oid"/> (with the <c>FoundryGate.Admin</c> role when
    /// <paramref name="isAdmin"/>), via <see cref="TestAuthHandler"/> headers. Use the plain
    /// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> for an anonymous caller.
    /// </summary>
    public HttpClient CreateClientAs(string oid, bool isAdmin = false, string? name = null, string? email = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oid);

        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.OidHeader, oid);

        if (isAdmin)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, RoleNames.Admin);
        }

        if (name is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name);
        }

        if (email is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email);
        }

        return client;
    }

    /// <summary>
    /// A fresh <see cref="AppDbContext"/> on the shared database, independent of any request scope —
    /// for arranging rows and asserting on what an endpoint persisted. Dispose it. Runs the real
    /// <see cref="TimestampInterceptor"/> against <see cref="TimeProvider"/>.
    /// </summary>
    public AppDbContext CreateDbContext()
    {
        // WebApplicationFactory builds the host lazily (first CreateClient()/Services access), and
        // that's where EnsureCreated + seeding run — a test that arranges rows before sending its
        // first request would otherwise hit an empty database with no tables.
        _ = Services;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new TimestampInterceptor(TimeProvider))
            .Options;

        return new AppDbContext(options);
    }

    /// <summary>
    /// Inserts a <see cref="User"/> and returns it (detached). Defaults to a fresh random oid and
    /// email so callers can seed as many as they like without unique-index collisions; pass
    /// <paramref name="entraObjectId"/> to line the row up with a <see cref="CreateClientAs"/> client.
    /// </summary>
    public async Task<User> SeedUserAsync(
        string? entraObjectId = null,
        string displayName = "Test User",
        string? email = null,
        bool isActive = true,
        Action<User>? configure = null)
    {
        await using var dbContext = CreateDbContext();

        var user = new User
        {
            EntraObjectId = entraObjectId ?? Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Email = email ?? $"{Guid.NewGuid():N}@contoso.test",
            IsActive = isActive,
        };
        configure?.Invoke(user);

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        return user;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("local");

        // AppSettings.ValidateRecursively() needs these populated; none is ever used to reach Azure or
        // SQL Server here (the DbContext registration is replaced below and bearer auth is never the
        // default scheme), so the factory stays hermetic even if appsettings.local.json changes.
        // UseSetting (host configuration), NOT ConfigureAppConfiguration: with minimal hosting the
        // latter's sources are appended only after Program.cs has already run `Configuration.Get<AppSettings>()`
        // and registered the singleton, so they never reach the bound options (verified — the
        // Gateway section below arrived in IConfiguration but not in AppSettings). Host settings are
        // in place before Program.cs's first line, which is also how UseEnvironment gets through.
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:FoundryGate"] =
                "Server=127.0.0.1,1;Database=FoundryGateTest;Connect Timeout=1;TrustServerCertificate=True",
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000000",
            ["AzureAd:Audience"] = "api://00000000-0000-0000-0000-000000000000",
            // Gateway addressing (#108) — resolved only by the fake FoundryClient below, never by ARM.
            ["Gateway:SubscriptionId"] = "00000000-0000-0000-0000-000000000001",
            ["Gateway:ResourceGroup"] = "rg-foundrygate-test",
            ["Gateway:FoundryAccountNames:0"] = PrimaryFoundryAccount,
            ["Gateway:FoundryAccountNames:1"] = SecondaryFoundryAccount,
            // APIM addressing (#36/#37) — served by the fake IApimManagementClient below, never by ARM;
            // the local-only key protector (#95) keeps Key Vault out of the tests.
            ["Gateway:ApimName"] = "apim-foundrygate-test",
            ["KeyProtection:Provider"] = "DataProtection",
        };
        foreach (var (key, value) in settings)
        {
            _ = builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            // Swap SQL Server → SQLite. EF Core 9+ registers the provider through
            // IDbContextOptionsConfiguration<TContext> entries as well as DbContextOptions<TContext>;
            // all of them must go or EF sees two providers configured on one context.
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>((serviceProvider, options) =>
                options.UseSqlite(_connection)
                    .AddInterceptors(serviceProvider.GetRequiredService<TimestampInterceptor>()));

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(TimeProvider);

            // ARM → in-memory fake. The real ArmClient registration stays but is lazy and never resolved.
            services.RemoveAll<IFoundryManagementClient>();
            services.AddSingleton<IFoundryManagementClient>(FoundryClient);

            // APIM management plane → in-memory fake (no Azure); exposed as its concrete type too so
            // tests can seed/inspect it. Data Protection → ephemeral keys, so the local key protector
            // never writes a key ring to the machine running the tests.
            services.RemoveAll<IApimManagementClient>();
            services.AddSingleton<FakeApimManagementClient>();
            services.AddSingleton<IApimManagementClient>(serviceProvider => serviceProvider.GetRequiredService<FakeApimManagementClient>());
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

            // Registered after Program.cs's AddMicrosoftIdentityWebApiAuthentication, so this
            // Configure<AuthenticationOptions> runs last and wins the default-scheme selection. The
            // JwtBearer scheme still exists; it just isn't consulted unless asked for by name.
            services.AddAuthentication(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        dbContext.Database.EnsureCreated();
        ReferenceDataSeeder.SeedAsync(dbContext).GetAwaiter().GetResult();

        return host;
    }

    private static FakeFoundryManagementClient CreateFoundryClient()
    {
        var client = new FakeFoundryManagementClient();
        client.AddAccount(PrimaryFoundryAccount);
        client.AddAccount(SecondaryFoundryAccount);
        return client;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
