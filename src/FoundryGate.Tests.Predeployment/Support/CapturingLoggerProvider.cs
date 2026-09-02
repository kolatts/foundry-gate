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
    private readonly List<(LogLevel Level, string Message)> _messages = [];

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

    /// <summary>
    /// The formatted messages only, each with the level it was written at, in order. Separate from
    /// <see cref="Entries"/> — which flattens state values in so a secret shipped as a property is
    /// still visible — because some assertions are about the <em>level</em>: a nightly job that no-ops
    /// on a deliberately disabled feature must say so at Information, not Warning or Error, or the
    /// alert it raises every night trains everyone to ignore this job (#151).
    /// </summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
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

    private void RecordMessage(LogLevel level, string message)
    {
        lock (_gate)
        {
            _messages.Add((level, message));
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            provider.Record(message);
            provider.RecordMessage(logLevel, message);

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
