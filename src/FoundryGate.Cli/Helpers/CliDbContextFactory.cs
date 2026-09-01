using FoundryGate.Data;
using FoundryGate.Data.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Cli.Helpers;

/// <summary>
/// Builds a standalone <see cref="AppDbContext"/> against a connection string, wired with the same
/// <see cref="TimestampInterceptor"/> the API/Functions hosts get via
/// <c>ServiceCollectionExtensions.AddFoundryGateData</c>. The Cli is not a DI host, so this is a
/// direct equivalent of that extension rather than a call through it.
/// </summary>
internal static class CliDbContextFactory
{
    public static AppDbContext Create(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var interceptor = new TimestampInterceptor(TimeProvider.System);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        return new AppDbContext(options);
    }
}
