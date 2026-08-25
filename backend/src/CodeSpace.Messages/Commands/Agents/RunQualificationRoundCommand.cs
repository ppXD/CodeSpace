using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Q-ops: mint ONE qualification round against the operator-staged hidden suite (the worker host's conventional
/// <c>~/.codespace/hidden-suite</c>) — spends real model budget and appends a DURABLE receipt, so it is
/// global-admin only and never recurring. An absent suite throws (misconfiguration, never a silent pass).
/// </summary>
public sealed record RunQualificationRoundCommand : ICommand<RunQualificationRoundResponse>, IRequireGlobalAdmin, IRequireTeamPermission
{
    /// <summary>A round launches real agent runs on the team's pool — the same consequence class as launching runs (the global-admin gate sits ABOVE this).</summary>
    public string RequiredPermission => Constants.TeamPermissions.RunsLaunch;

    public required string Mode { get; init; }

    public required string CapabilityKey { get; init; }

    /// <summary>The one-sided 95% Wilson LOWER bound the round must clear for a Sealed grant — the operator's claim bar.</summary>
    public required double MinSolveRateLowerBound { get; init; }

    public double MinEvaluatorHealth { get; init; } = 0.9;

    public int ValidityDays { get; init; } = 30;

    /// <summary>The verifier bundle: which harness/model runs the round. Null harness/model ⇒ the tasks' own declarations.</summary>
    public string? Harness { get; init; }

    public string? Model { get; init; }

    public Guid? ModelCredentialId { get; init; }

    public AgentAutonomyLevel? Autonomy { get; init; }
}

/// <summary>The minted round: the standing granted, its statistics, and the immutable receipt's identity.</summary>
public sealed record RunQualificationRoundResponse
{
    public required PerformanceQualification Granted { get; init; }
    public required double SolveRateLowerBound { get; init; }
    public required double EvaluatorHealth { get; init; }
    public required int Solved { get; init; }
    public required int Total { get; init; }
    public required Guid ReceiptId { get; init; }
    public required string SuiteDigest { get; init; }
}
