using Microsoft.Extensions.Logging;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// <see cref="ILoggerProvider"/> that records every log call — the formatted message <em>and</em> the
/// raw structured state (each <c>{Placeholder}</c> value), because a secret that never appears in a
/// message template could still be shipped as a property. Used to prove the key service never logs
/// key material. Create loggers with <see cref="CreateLogger{T}"/>.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly List<string> _entries = [];

    /// <summary>Every recorded message and state value, in order.</summary>
    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>A typed logger that records into this provider at every level.</summary>
    public ILogger<T> CreateLogger<T>() =>
        LoggerFactory.Create(builder => builder.AddProvider(this).SetMinimumLevel(LogLevel.Trace)).CreateLogger<T>();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private void Record(string entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            provider.Record(formatter(state, exception));

            if (state is IReadOnlyList<KeyValuePair<string, object?>> properties)
            {
                foreach (var property in properties)
                {
                    provider.Record(property.Value?.ToString() ?? string.Empty);
                }
            }

            if (exception is not null)
            {
                provider.Record(exception.ToString());
            }
        }
    }
}
