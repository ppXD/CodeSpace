namespace CodeSpace.Messages.Agents;

/// <summary>The outcome of answering a run's pending supervisor ask (a data noun, Rule 18.1). <c>Resumed</c> is false when another surface's answer won the race — the ask is settled either way.</summary>
public sealed record SupervisorAskAnswerOutcome
{
    public required bool Resumed { get; init; }
}
