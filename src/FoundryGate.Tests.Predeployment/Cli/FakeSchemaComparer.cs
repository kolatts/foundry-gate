using FoundryGate.Cli.Commands.Db.Compare;

namespace FoundryGate.Tests.Predeployment.Cli;

/// <summary>Scripted <see cref="ISchemaComparer"/> for <see cref="CompareRunnerTests"/> — no real DacFx call, no real database.</summary>
public sealed class FakeSchemaComparer : ISchemaComparer
{
    private readonly SchemaCompareOutcome _compareOutcome;
    private readonly SchemaComparePublishOutcome? _publishOutcome;
    private bool _compared;

    public FakeSchemaComparer(SchemaCompareOutcome compareOutcome, SchemaComparePublishOutcome? publishOutcome = null)
    {
        _compareOutcome = compareOutcome;
        _publishOutcome = publishOutcome;
    }

    /// <summary>How many times <see cref="Publish"/> was called — <see cref="CompareRunner"/> must never call it when there are no differences.</summary>
    public int PublishCallCount { get; private set; }

    public SchemaCompareOutcome Compare()
    {
        _compared = true;
        return _compareOutcome;
    }

    public SchemaComparePublishOutcome Publish()
    {
        if (!_compared)
        {
            throw new InvalidOperationException($"{nameof(Compare)}() must run before {nameof(Publish)}().");
        }

        PublishCallCount++;
        return _publishOutcome ?? throw new InvalidOperationException("Test did not configure a publish outcome.");
    }
}
