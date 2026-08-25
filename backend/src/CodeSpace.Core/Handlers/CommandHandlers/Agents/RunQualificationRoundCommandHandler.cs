using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="IQualificationRunner.QualifyAsync"/>; the team (whose pool pays) comes from <see cref="ICurrentTeam"/>, never the wire.</summary>
public sealed class RunQualificationRoundCommandHandler : IRequestHandler<RunQualificationRoundCommand, RunQualificationRoundResponse>
{
    private readonly IQualificationRunner _runner;
    private readonly ICurrentTeam _currentTeam;

    public RunQualificationRoundCommandHandler(IQualificationRunner runner, ICurrentTeam currentTeam)
    {
        _runner = runner;
        _currentTeam = currentTeam;
    }

    public async Task<RunQualificationRoundResponse> Handle(RunQualificationRoundCommand request, CancellationToken cancellationToken)
    {
        var spec = new QualificationSpec { MinSolveRateLowerBound = 0.0, MinEvaluatorHealth = request.MinEvaluatorHealth, ValidityDays = request.ValidityDays };
        var selection = new BenchmarkAgentSelection { Harness = request.Harness, Model = request.Model, ModelCredentialId = request.ModelCredentialId, Autonomy = request.Autonomy };

        var outcome = await _runner.QualifyAsync(request.Mode, request.CapabilityKey, spec, _currentTeam.Id!.Value, selection, cancellationToken).ConfigureAwait(false);

        return new RunQualificationRoundResponse
        {
            Granted = outcome.Granted,
            SolveRateLowerBound = outcome.SolveRateLowerBound,
            EvaluatorHealth = outcome.Score.EvaluatorHealth,
            Solved = outcome.Score.Solved,
            Total = outcome.Score.Total,
            ReceiptId = outcome.ReceiptId,
            SuiteDigest = outcome.SuiteDigest,
        };
    }
}
