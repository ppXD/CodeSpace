using CodeSpace.Core.Handlers.CommandHandlers.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the minting entry threads the operator's bar VERBATIM — a handler that softened (or hardened) the
/// requested MinSolveRateLowerBound/health/validity on the way to the runner would be a silent claim-bar
/// laundering channel. Pins the spec, the selection, and the paying team all arriving exactly as sent.
/// </summary>
[Trait("Category", "Unit")]
public class RunQualificationRoundHandlerTests
{
    [Fact]
    public async Task The_operators_bar_reaches_the_runner_verbatim()
    {
        var runner = new CapturingRunner();
        var teamId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var handler = new RunQualificationRoundCommandHandler(runner, new FixedTeam(teamId));

        var response = await handler.Handle(new RunQualificationRoundCommand
        {
            Mode = "supervisor", CapabilityKey = "git-branch",
            MinSolveRateLowerBound = 0.83, MinEvaluatorHealth = 0.95, ValidityDays = 14,
            Harness = "claude-code", Model = "m-1", ModelCredentialId = credentialId,
        }, CancellationToken.None);

        runner.Spec!.MinSolveRateLowerBound.ShouldBe(0.83, "the claim bar is the OPERATOR'S, threaded verbatim — softening it here would launder the seal");
        runner.Spec.MinEvaluatorHealth.ShouldBe(0.95);
        runner.Spec.ValidityDays.ShouldBe(14);
        runner.TeamId.ShouldBe(teamId, "the paying team comes from ICurrentTeam, never the wire");
        runner.Selection!.Harness.ShouldBe("claude-code");
        runner.Selection.ModelCredentialId.ShouldBe(credentialId);
        response.Granted.ShouldBe(PerformanceQualification.Sealed);
        response.ReceiptId.ShouldBe(runner.ReceiptId);
    }

    private sealed class CapturingRunner : IQualificationRunner
    {
        public QualificationSpec? Spec; public BenchmarkAgentSelection? Selection; public Guid TeamId; public Guid ReceiptId = Guid.NewGuid();

        public Task<QualificationOutcome> QualifyAsync(string mode, string capabilityKey, QualificationSpec spec, Guid teamId, BenchmarkAgentSelection selection, CancellationToken cancellationToken)
        {
            Spec = spec; Selection = selection; TeamId = teamId;
            return Task.FromResult(new QualificationOutcome(new CorpusCellScore { Solved = 19, Unsolved = 1, Abstained = 0, InfraUnknown = 0 }, 0.75, PerformanceQualification.Sealed, ReceiptId, "sha256:x"));
        }
    }

    private sealed class FixedTeam : ICurrentTeam
    {
        private readonly Guid _id;
        public FixedTeam(Guid id) => _id = id;
        public Guid? Id => _id;
        public bool IsSet => true;
    }
}
