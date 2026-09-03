namespace CodeSpace.Messages.Agents;

/// <summary>
/// C2 — what a bounded walk of a repo-less run's scratch world actually took, and what it left. Both numbers, never
/// just the first: a walk that captured three files and refused forty is a materially different fact from one that
/// captured three and refused none, and only the pair makes an over-limit capture VISIBLE instead of silent. Rides
/// the capture promise's commit-time facts (<c>AgentRunExecutor.CaptureFactsOf</c>), which is where a shortfall
/// against what a world held becomes readable.
/// </summary>
public sealed record UndeclaredCaptureOutcome
{
    /// <summary>The nothing-happened outcome — no walk ran (there was no scratch world), so neither number claims anything.</summary>
    public static readonly UndeclaredCaptureOutcome None = new();

    /// <summary>How many UNDECLARED scratch files the walk minted typed artifact-manifest rows for.</summary>
    public int Captured { get; init; }

    /// <summary>How many files the walk SAW inside the scratch world and did not take — a non-allowlisted extension, a dotfile, or a per-walk file/byte limit already reached. Nonzero is the shortfall signal.</summary>
    public int Refused { get; init; }
}
