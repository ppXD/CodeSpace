using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Sessions.Room;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// Launch Extremis P21-8a — per-artifact / per-repository verification truth. A multi-repo turn can have one
/// repository cleanly verified and a sibling withheld; folding both into the run's single Verified chip
/// (<see cref="FinalAnswerBlock.Verified"/>) hides exactly that split. These pin
/// <see cref="RoomProjector.ArtifactVerificationOf"/> / <see cref="RoomProjector.VerificationsForRepository"/> /
/// <see cref="RoomProjector.VerificationsForAgent"/> — built on the SAME graded/failed predicate
/// (<c>RoomProjector.IsGradedUnit</c>) <see cref="RoomProjector.UnitGrades"/> folds the run-level verdict from,
/// reused rather than re-defined.
///
/// <para>Tier: Unit — every fold under test is pure over in-memory <see cref="SupervisorAgentResult"/> facts.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RoomArtifactVerificationTests
{
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly IReadOnlyDictionary<Guid, RoomAgentLogSummary> NoLogs = new Dictionary<Guid, RoomAgentLogSummary>();

    [Theory]
    [InlineData(true, "tests-passed", false, true, true, RoomOracleProtection.None)]
    [InlineData(false, "tests-failed-exit-1", false, true, false, RoomOracleProtection.None)]
    [InlineData(null, null, false, false, null, RoomOracleProtection.None)]
    [InlineData(true, "tests-passed — graded on the candidate's own solution.sh", false, true, true, RoomOracleProtection.Subject)]
    [InlineData(true, "oracle: graded UNPROTECTED (no base recorded)", false, true, true, RoomOracleProtection.Unanchored)]
    [InlineData(true, "not-applicable: no changes were expected and none were produced", false, false, null, RoomOracleProtection.None)]
    public void One_units_row_reads_the_same_graded_line_UnitGrades_uses(bool? acceptancePassed, string? detail, bool waived, bool expectedRan, bool? expectedPassed, RoomOracleProtection expectedProtection)
    {
        var verification = RoomProjector.ArtifactVerificationOf(Unit(acceptancePassed, detail, waived), "backend", NoLogs);

        verification.Ran.ShouldBe(expectedRan);
        verification.Passed.ShouldBe(expectedPassed);
        verification.OracleProtection.ShouldBe(expectedProtection);
        verification.ArtifactOrRepositoryRef.ShouldBe("backend");
        verification.CheckKind.ShouldBe("acceptance");
    }

    [Fact]
    public void A_WAIVED_unit_is_unrun_never_a_fabricated_pass_or_a_reported_rejection()
    {
        // A human authorized forgoing this unit's verification; its executor-level grade can still read FAILED. The
        // row must report NEITHER a rejection (the waive is what let the work through) NOR a pass (WAIVED != PASSED).
        var verification = RoomProjector.ArtifactVerificationOf(Unit(false, "tests-failed-exit-1", waived: true), "backend", NoLogs);

        verification.Ran.ShouldBeFalse("a waive verifies nothing — it must not read as an executed check");
        verification.Passed.ShouldBeNull();
    }

    [Fact]
    public void An_incomplete_log_never_cancels_an_otherwise_verified_delivery()
    {
        // The P21 invariant, restated at the field level: Passed and LogsComplete come off two INDEPENDENT facts, so
        // a still-finalizing log stream must not blank out (or flip) a check that genuinely passed.
        var logs = new Dictionary<Guid, RoomAgentLogSummary> { [Agent] = new(RoomAgentLogStatus.Incomplete, 1, "1 stream · 1 unavailable") };

        var verification = RoomProjector.ArtifactVerificationOf(Unit(true, "tests-passed"), "backend", logs);

        verification.Passed.ShouldBe(true, "the check's own verdict is untouched by the log's own health");
        verification.LogsComplete.ShouldBe(false, "the log carries its OWN, separate account");
    }

    [Theory]
    [InlineData(RoomAgentLogStatus.Verified, true)]
    [InlineData(RoomAgentLogStatus.Captured, true)]
    [InlineData(RoomAgentLogStatus.Finalizing, false)]
    [InlineData(RoomAgentLogStatus.Incomplete, false)]
    public void LogsComplete_reads_whether_the_stream_SETTLED_not_whether_it_was_integrity_verified(RoomAgentLogStatus status, bool expectedComplete)
    {
        var logs = new Dictionary<Guid, RoomAgentLogSummary> { [Agent] = new(status, 1, "detail") };

        RoomProjector.ArtifactVerificationOf(Unit(true, "tests-passed"), "backend", logs).LogsComplete.ShouldBe(expectedComplete);
    }

    [Fact]
    public void An_agent_that_declared_no_log_stream_leaves_LogsComplete_unsaid()
    {
        RoomProjector.ArtifactVerificationOf(Unit(true, "tests-passed"), "backend", NoLogs).LogsComplete.ShouldBeNull();
    }

    [Fact]
    public void The_evidence_artifact_id_rides_straight_off_the_recorded_grade()
    {
        var evidenceId = Guid.NewGuid();

        RoomProjector.ArtifactVerificationOf(Unit(false, "tests-failed-exit-1") with { AcceptanceEvidenceId = evidenceId }, "backend", NoLogs)
            .EvidenceArtifactId.ShouldBe(evidenceId);
    }

    [Fact]
    public void A_long_detail_is_clipped_so_the_row_stays_bounded()
    {
        var clipped = RoomProjector.ArtifactVerificationOf(Unit(false, new string('x', 400)), "backend", NoLogs).Detail;

        clipped!.Length.ShouldBeLessThan(250);
        clipped.ShouldEndWith("…");
    }

    // ── per-repository attribution ──

    [Fact]
    public void A_multi_repo_units_verification_lands_on_ONLY_the_repository_it_touched()
    {
        var apiId = Guid.NewGuid();
        var webId = Guid.NewGuid();
        var results = new[]
        {
            Unit(true, "tests-passed") with { RepositoryResults = new[] { new RepositoryRunResult { RepositoryId = apiId, Alias = "api" } } },
            Unit(false, "tests-failed-exit-1") with { RepositoryResults = new[] { new RepositoryRunResult { RepositoryId = webId, Alias = "web" } } },
        };

        var apiVerifications = RoomProjector.VerificationsForRepository(results, NoLogs, singleRepoRun: false, apiId, "api");
        var webVerifications = RoomProjector.VerificationsForRepository(results, NoLogs, singleRepoRun: false, webId, "web");

        apiVerifications.ShouldHaveSingleItem();
        apiVerifications[0].Passed.ShouldBe(true, "api's own row must never see web's rejection");

        webVerifications.ShouldHaveSingleItem();
        webVerifications[0].Passed.ShouldBe(false, "web's own row must never be papered over by api's pass — no collapsed overall");
    }

    [Fact]
    public void A_single_repo_runs_legacy_shaped_unit_still_attributes_to_its_sole_repository()
    {
        // Pre-multi-repo compat shape: RepositoryResults is empty and the top-level fields ARE the one repo.
        var unit = Unit(true, "tests-passed") with { ProducedBranch = "codespace/agent/fix" };

        RoomProjector.VerificationsForRepository(new[] { unit }, NoLogs, singleRepoRun: true, Guid.NewGuid(), "primary").ShouldHaveSingleItem();
    }

    [Fact]
    public void A_coordinator_only_unit_that_touched_no_repository_is_not_attributed_to_any_delivery()
    {
        // Guards the single-repo fallback: an agent with EMPTY RepositoryResults and no branch/files must not be
        // mistaken for "the run's one repo" merely because it is the only delivery around.
        var coordinator = Unit(true, "tests-passed");

        RoomProjector.VerificationsForRepository(new[] { coordinator }, NoLogs, singleRepoRun: true, Guid.NewGuid(), "primary").ShouldBeEmpty();
    }

    [Fact]
    public void A_multi_repo_run_never_falls_back_to_attributing_an_untagged_unit_to_every_repository()
    {
        // Even a unit that plainly delivered something (a pushed branch) must NOT be attributed to a repository it
        // never tagged once the run has more than one delivery — singleRepoRun gates the legacy fallback outright.
        var untagged = Unit(true, "tests-passed") with { ProducedBranch = "codespace/agent/fix" };

        RoomProjector.VerificationsForRepository(new[] { untagged }, NoLogs, singleRepoRun: false, Guid.NewGuid(), "api").ShouldBeEmpty();
    }

    // ── per-file attribution ──

    [Fact]
    public void A_files_verification_is_its_OWN_producing_agents_grade_only()
    {
        var producer = Guid.NewGuid();
        var results = new[] { Unit(true, "tests-passed") with { AgentRunId = producer }, Unit(false, "tests-failed-exit-1") with { AgentRunId = Guid.NewGuid() } };

        var verifications = RoomProjector.VerificationsForAgent(results, NoLogs, producer);

        verifications.ShouldHaveSingleItem();
        verifications[0].Passed.ShouldBe(true);
    }

    private static SupervisorAgentResult Unit(bool? acceptancePassed, string? detail, bool waived = false) => new()
    {
        AgentRunId = Agent,
        Status = acceptancePassed == false ? "Failed" : "Succeeded",
        AcceptancePassed = acceptancePassed,
        AcceptanceDetail = detail,
        AcceptanceVerdict = waived ? VerificationDisposition.Waived : null,
    };
}
