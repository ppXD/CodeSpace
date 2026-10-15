using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// Live planner provider → typed acceptance mapping → actual CLI/file grading with a negative oracle. This tests an
/// explicitly prepared grading directory, not automatic repository-free executor acceptance.
///
/// <para><b>Report-only (<c>gating: false</c>) until it has a passing history</b> — this repo's rule for a NEW arm, and
/// this one has no passing run at all. Every live attempt so far has ended in the same place: the model's reply carried
/// <c>formatVersion</c> but omitted the acceptance payload (<c>argv</c> / <c>artifactPaths</c>), so the typed contract
/// refused to bind it. The wire schema now declares <c>kind</c> ahead of every property that depends on it, because a
/// grammar-constrained decoder writes properties in declared order and so had to decide on the payload before it had
/// chosen the oracle; this arm is where that ordering is measured against a real model. An arm that has never once
/// passed cannot tell a regression from its own unmet precondition, so gating it only reds the blessed wire for what the
/// model has always done. The verdict is still REPORTED on every run — the contract-miss note included — so the moment
/// the model starts binding, the passing history the rule asks for exists in the job summaries, and the arm can be
/// promoted by a one-word change here.</para>
///
/// <para>That precondition now arrives as DATA rather than as an exception: an acceptance the contract cannot bind is
/// dropped and recorded on the plan instead of failing the planner. The miss is therefore read off
/// <c>PlannedWorkflow.DroppedAcceptances</c> and returned as the same informational verdict — otherwise the very
/// degrade that keeps live plans alive would turn this arm's oldest known precondition into a hard assertion red.</para>
///
/// <para>The file obligation is a JSON deliverable paired with a schema, not a bare existence check: a planner-authored
/// <c>ArtifactPresent</c> is never kept as authored (<c>PlannerAcceptanceDraft.ReconcileArtifactPresent</c> promotes it
/// to the content oracle it is paired with, or drops it as self-certifying), so the oracle this arm grades is the
/// promoted <c>ArtifactSchema</c>, which only validates JSON.</para>
///
/// <para>Only its GATING is soft. Everything the arm asserts with Shouldly still reds: <c>RealModelGate</c> excludes a
/// <c>ShouldAssertException</c> from the report-only catch deliberately, so the grader facts below — the oracle
/// accepting the correct fixture and rejecting the wrong one, the persisted evidence artifact — keep their hard
/// character. What is soft is the model's own contract binding, which is all this demotion covers.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Grader")]
public sealed class RealModelPlannerAcceptanceE2ETests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Live_planner_keeps_executable_content_checks_separate_from_file_obligations_and_the_real_oracle_rejects_wrong_content()
    {
        var connection = new Gateway(Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar), Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar), Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar));
        var present = new[] { connection.BaseUrl, connection.ApiKey, connection.Model }.Count(value => value is not null);
        if (present == 0) throw RealModelGate.ReportSkipped("Anthropic", "CODESPACE_LLM_* absent; live typed planner and CLI oracle not evaluated");
        present.ShouldBe(3, "partial live provider configuration is an error, not a passing or skipped gate");
        File.Exists("/bin/sh").ShouldBeTrue("the real CLI oracle requires /bin/sh");

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var modelRowId = await SeedGatewayAsync(teamId, userId, connection);
        using var scope = fixture.BeginScopeAs(userId, teamId);

        // Not AssessLiveBestOfNAsync: a best-of-N capability floor exists to keep a GATING arm flake-safe, and this arm
        // does not gate. One reported attempt is the whole measurement, at a fourth of the token cost.
        await RealModelGate.AssessLiveAsync("Anthropic", async () =>
        {
            PlannedWorkflow plan;

            try
            {
                plan = await scope.Resolve<IWorkflowPlanner>().PlanAsync(new WorkflowPlanRequest
                {
                    TeamId = teamId, BrainModelId = modelRowId,
                    TaskText = "Author exactly two independent subtasks for validating a prepared report.txt file. "
                        + "One subtask MUST use TestsPass to verify the file's entire content equals accepted (with optional trailing newline), and return nonzero on incorrect content. "
                        + "Choose an exact read-only argv using the available /bin/sh, cat, test and printf commands; do not write or repair files. "
                        + "The second subtask MUST independently require a separate deliverable report.json to exist as a JSON object whose status property is the string accepted: use ArtifactPresent for report.json paired with a schema that requires status to be a string whose only allowed value is accepted (a bare existence check is not kept). "
                        + "These are distinct obligations: mere file presence cannot establish correct content. Both acceptance objects are required. "
                        + "The checks will be graded in an explicitly prepared directory. Do not claim that repository-free agent execution or dependency readiness has been verified.",
                }, CancellationToken.None);
            }
            catch (Exception ex) when (IsPlannerContractMiss(ex))
            {
                // RETURNED as a verdict, never rethrown. A reply that never bound to the typed acceptance contract is a
                // MODEL capability miss — #1837 already fed the typed violations back for a bounded re-ask, and this is
                // what is left once that budget is spent. Raw, it left the arm as an engine exception; returned, it is
                // the INFORMATIONAL line that carries the miss note into the job summary, which is the only way the
                // arm accumulates the passing history the class doc says it needs.
                return (false, $"the live planner's reply never bound to the typed acceptance contract, even after the provider's bounded re-ask — {ex.GetType().Name}: {ex.Message}");
            }

            // The same verdict, for the same miss, now that it no longer throws: an acceptance the typed contract
            // could not bind is dropped and NAMED on the plan. Returning it keeps the arm's hard assertions about
            // GRADING hard while leaving the model's own contract binding soft, which is the whole demotion.
            if (plan.DroppedAcceptances is { Count: > 0 } dropped) return (false, DescribeDrops(dropped));

            plan.AuthoredByModel.ShouldNotBeNullOrWhiteSpace("the response must come from the pinned real provider path");
            plan.Subtasks.Count.ShouldBe(2);
            plan.Subtasks.ShouldAllBe(item => item.Acceptance != null);
            plan.Subtasks.ShouldNotContain(item => item.Acceptance!.Kind == BenchmarkGradingKind.ArtifactPresent, "ReconcileArtifactPresent either promotes an ArtifactPresent to its paired content oracle or drops it by name, so none survives a plan that dropped nothing");

            var command = plan.Subtasks.FirstOrDefault(item => item.Acceptance!.Kind == BenchmarkGradingKind.TestsPass)?.Acceptance;
            var content = plan.Subtasks.FirstOrDefault(item => item.Acceptance!.Kind == BenchmarkGradingKind.ArtifactSchema)?.Acceptance;

            if (command is null || content is null)
                return (false, $"the live planner's acceptance kinds were [{string.Join(", ", plan.Subtasks.Select(item => item.Acceptance!.Kind))}] where one TestsPass and one ArtifactSchema (an ArtifactPresent paired with a schema, promoted) were asked for");

            content.Command.ShouldBe(new[] { "report.json" }, "the model must author literal file obligations, not test/-f argv in the path field");
            content.Schema.ShouldNotBeNull("an ArtifactSchema oracle only exists when a schema was authored alongside it");
            command.Command.Count.ShouldBeGreaterThan(0);

            var directory = Path.Combine(Path.GetTempPath(), $"cs-live-planner-grade-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var report = Path.Combine(directory, "report.txt");
                var data = Path.Combine(directory, "report.json");
                await File.WriteAllTextAsync(report, "accepted\n");
                await File.WriteAllTextAsync(data, """{"status":"accepted"}""");
                var grader = scope.Resolve<ISupervisorAcceptanceGrader>();
                var correct = await grader.GradeDirectoryAsync(directory, command, teamId, 20, CancellationToken.None);
                correct.Passed.ShouldBeTrue(correct.Detail);
                correct.EvidenceArtifactId.ShouldNotBeNull("an actual command verdict carries persisted oracle evidence");

                // The schema is the MODEL's own authoring, so a schema that rejects the very document the task describes is
                // a model-quality miss reported like any other, not a grader fault.
                var accepted = await grader.GradeDirectoryAsync(directory, content, teamId, 20, CancellationToken.None);
                if (!accepted.Passed) return (false, $"the live planner's ArtifactSchema rejected the correct report.json — {accepted.Detail}");

                await File.WriteAllTextAsync(report, "incorrect\n");
                var wrong = await grader.GradeDirectoryAsync(directory, command, teamId, 20, CancellationToken.None);
                wrong.Passed.ShouldBeFalse("the exact same model-authored command must reject the negative fixture");
                wrong.Class.ShouldBe(GradeFailureClass.Genuine);
                wrong.EvidenceArtifactId.ShouldNotBeNull();
                (await grader.GradeDirectoryAsync(directory, content, teamId, 20, CancellationToken.None)).Passed.ShouldBeTrue("the report.json obligation is independent of report.txt's content");
                File.Delete(data);
                (await grader.GradeDirectoryAsync(directory, content, teamId, 20, CancellationToken.None)).Passed.ShouldBeFalse("a missing report.json must fail the schema oracle closed");
            }
            finally { Directory.Delete(directory, recursive: true); }

            return (true, $"the live planner authored a {command.Command.Count}-token TestsPass argv that accepted the correct fixture and rejected the wrong one, plus an independent ArtifactSchema obligation on report.json (an ArtifactPresent paired with a schema, promoted) that accepted the correct document and failed closed once the file was gone");
        }, gating: false);
    }

    /// <summary>
    /// The plan's dropped acceptances, told apart by WHO dropped them — the two mean different things to a reader. A
    /// <b>payload omitted</b> drop is the model's reply failing the typed contract (the bind refused it, even after the
    /// provider's bounded re-ask). A <b>P2.6 policy</b> drop is a reply that BOUND fine: it authored a bare
    /// <c>ArtifactPresent</c> (or one for an undeclared path), which the server refuses to keep as self-certifying.
    /// </summary>
    private static string DescribeDrops(IReadOnlyList<DroppedAcceptance> dropped)
    {
        var policy = dropped.Where(IsPolicyDrop).ToList();
        var omitted = dropped.Where(drop => !IsPolicyDrop(drop)).ToList();
        var parts = new List<string>();

        if (omitted.Count > 0) parts.Add($"{omitted.Count} acceptance(s) the typed contract could not bind (payload omitted or malformed), even after the provider's bounded re-ask — {JoinDrops(omitted)}");
        if (policy.Count > 0) parts.Add($"{policy.Count} ArtifactPresent acceptance(s) that bound but were dropped by the P2.6 self-certifying policy (no paired ArtifactSchema or LlmJudge content check) — {JoinDrops(policy)}");

        return "the live planner authored " + string.Join(", and ", parts);
    }

    /// <summary>Both P2.6 refusals (<c>PlannerAcceptanceDraft.InadmissibleArtifactPresentReason</c>) open with this phrase; no bind-level reason does.</summary>
    private static bool IsPolicyDrop(DroppedAcceptance drop) => drop.Reason.StartsWith("Planner acceptance ArtifactPresent for '", StringComparison.Ordinal);

    private static string JoinDrops(IEnumerable<DroppedAcceptance> dropped) => string.Join("; ", dropped.Select(drop => $"{drop.SubtaskId} ({drop.Kind}): {drop.Reason}"));

    /// <summary>
    /// Whether <paramref name="ex"/> is the live planner's REPLY failing the PLAN contract, rather than infrastructure
    /// breaking underneath it. Two shapes, both meaning "the model could not author a conforming plan": the provider's
    /// bounded re-ask spent with the second reply still invalid (a Malformed <see cref="LlmApiException"/> — the
    /// categories the gate's own <c>IsGatewayInfraFailure</c> deliberately excludes from infra), and
    /// <c>LlmWorkflowPlanner.Deserialize</c>'s <see cref="InvalidOperationException"/> wrapper over a
    /// <see cref="JsonException"/> for a reply that is well-formed JSON yet binds to no <c>PlannedWorkflow</c>.
    ///
    /// <para>What no longer arrives here is an ACCEPTANCE miss: those are dropped and recorded on the plan, and the
    /// caller reads them off <c>DroppedAcceptances</c>. Both clauses now mean a plan-SHAPE failure.</para>
    ///
    /// <para>Everything else PROPAGATES, deliberately. A gateway timeout / transport drop / rate limit / auth failure is
    /// routed by the gate to a non-gating infra skip, and a real regression reds — including the two brain-RESOLUTION
    /// <see cref="InvalidOperationException"/>s <c>PlanAsync</c> throws ("no structured provider has a pool model" /
    /// "the pinned brain model is not eligible"), which are WIRING faults this arm must never launder into a
    /// model-capability note. Requiring the inner <see cref="JsonException"/> is what tells those two apart from the
    /// bind failure; <c>Deserialize</c>'s remaining empty-plan throw carries no inner exception either, and needs no
    /// clause here because the response validator rejects an empty subtask list first and it surfaces as Malformed.</para>
    /// </summary>
    private static bool IsPlannerContractMiss(Exception ex) => ex switch
    {
        LlmApiException { Category: LlmErrorCategory.Malformed } => true,
        JsonException => true,
        InvalidOperationException { InnerException: JsonException } => true,
        _ => false,
    };

    private async Task<Guid> SeedGatewayAsync(Guid teamId, Guid userId, Gateway connection)
    {
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credentialId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential { Id = credentialId, TeamId = teamId, Provider = "Anthropic", DisplayName = "live typed planner", EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(connection.ApiKey!), BaseUrl = connection.BaseUrl!.TrimEnd('/'), Status = CredentialStatus.Active });
        var modelId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = modelId, ModelCredentialId = credentialId, ModelId = connection.Model!, Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();
        return modelId;
    }

    private sealed record Gateway(string? BaseUrl, string? ApiKey, string? Model);
    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);
}
