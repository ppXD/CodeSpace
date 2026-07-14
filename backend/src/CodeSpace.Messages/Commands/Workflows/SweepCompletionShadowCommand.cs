using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>P2a-4: sweep recent terminal contract-era runs into durable shadow assessments (composer chain; Lock Clause 1 — never mutates a terminal).</summary>
public sealed record SweepCompletionShadowCommand : ICommand<int>
{
    /// <summary>Runs examined per sweep — bounds each tick's compose cost.</summary>
    public int BatchSize { get; init; } = 50;
}
