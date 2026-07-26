using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 High-fidelity shared probe: the honest OUTCOME word on a LIVE supervisor terminal.
///
/// <para>A supervisor run answers "did it work?" with <c>Success</c> whether the model solved the goal, gave up,
/// abstained, or failed its own acceptance checks — the graph completed either way. The outcome word is therefore the
/// only signal that separates those, and it is the one an operator reads in the runs index.</para>
///
/// <para><b>Why a live arc and not a flow test.</b> Scripted coverage authors the stop it then classifies, so it can
/// only ever prove the classifier agrees with itself. What it cannot reach is the WIRE: that the engine ran the
/// derivation at terminalization, against the last TERMINAL stop rather than the last decision, persisted it to the
/// column, and that the read model carried it to the index. Every one of those is a place the word can vanish while
/// every scripted test stays green.</para>
///
/// <para><b>Why it compares a derivation, not a literal word.</b> A live model may legitimately end an arc in any of
/// several ways, so pinning "Succeeded" would make this a coin flip on model mood. Re-deriving from the same durable
/// bytes keeps the assertion true under every ending while still failing the moment the engine's own path breaks.</para>
/// </summary>
internal static class HonestOutcomeProbe
{
    /// <summary>
    /// Every word the backend can put in the column. Held here rather than derived from the enum on purpose: this is
    /// the set the FRONTEND is expected to understand, so a new <see cref="SupervisorStopKind"/> variant reaching a
    /// live run must fail here loudly instead of rendering as a silent "Done" in the index.
    /// </summary>
    private static readonly HashSet<string> KnownWords = new(StringComparer.Ordinal)
    {
        nameof(SupervisorStopKind.Succeeded), nameof(SupervisorStopKind.GaveUp),
        nameof(SupervisorStopKind.Forced), nameof(SupervisorStopKind.NeedsClarification),
        SupervisorOutcome.AcceptanceFailedOutcome,
    };

    /// <summary>
    /// Checks the outcome word on a run that reached a CLEAN terminal. Returns a fault description, or null when the
    /// word is honest. Call on any non-Failure terminal — a run that missed its goal is the MOST valuable case, since
    /// the degraded words are only ever earned there.
    /// </summary>
    public static async Task<string?> FaultAsync(PostgresFixture fixture, Guid runId, Guid teamId)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        if (run.Status is not (WorkflowRunStatus.Success or WorkflowRunStatus.Cancelled)) return null;

        var stops = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderBy(d => d.Sequence)
            .Select(d => new { d.Status, d.PayloadJson, d.OutcomeJson })
            .ToListAsync();

        var terminal = stops.LastOrDefault(s => SupervisorDecisionStateMachine.IsTerminal(s.Status));

        if (terminal is null)
            return run.Outcome is null
                ? null   // no terminal stop, no word owed — a parked or agent-only run
                : $"the run carries outcome '{run.Outcome}' with NO terminal stop on its tape — the word was derived from something that is not a finished decision";

        var expected = SupervisorOutcome.HonestOutcome(terminal.PayloadJson, terminal.OutcomeJson);

        if (run.Outcome != expected)
            return $"the run persisted outcome '{run.Outcome ?? "(null)"}' but its own terminal stop reads '{expected}' — the engine's derivation did not reach the row";

        if (!KnownWords.Contains(expected))
            return $"a live run earned the outcome word '{expected}', which the runs index does not know how to render — it would show as an ordinary success. Add it to OUTCOME_WORDS in frontend/src/lib/runStatus.ts";

        var page = await scope.Resolve<IWorkflowService>().ListTeamRunsAsync(teamId, new RunListFilter(), cursor: null, limit: 200, CancellationToken.None);
        var listed = page.Items.FirstOrDefault(i => i.Id == runId);

        if (listed is null)
            return $"the terminal run is absent from its own team's runs index — the operator has no row to read the outcome from";

        if (listed.Outcome != run.Outcome)
            return $"the runs index projects outcome '{listed.Outcome ?? "(null)"}' while the row holds '{run.Outcome}' — the operator reads the projection, so a drop there hides the word entirely";

        Console.WriteLine($"[honest-outcome] run {runId} terminalized {run.Status} as '{run.Outcome}' (derived from its terminal stop, and matching on the runs index)");
        return null;
    }
}
