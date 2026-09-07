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

/// <summary>Live planner provider → typed acceptance mapping → actual CLI/file grading with a negative oracle. This tests an explicitly prepared grading directory, not automatic repository-free executor acceptance.</summary>
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

        await RealModelGate.AssessLiveBestOfNAsync("Anthropic", async () =>
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
                        + "The second subtask MUST independently require report.txt to exist using ArtifactPresent. "
                        + "These are distinct obligations: mere file presence cannot establish correct content. Both acceptance objects are required. "
                        + "The checks will be graded in an explicitly prepared directory. Do not claim that repository-free agent execution or dependency readiness has been verified.",
                }, CancellationToken.None);
            }
            catch (Exception ex) when (IsPlannerContractMiss(ex))
            {
                // RETURNED as a failed attempt, never rethrown. A reply that never bound to the typed acceptance
                // contract is a MODEL capability miss — #1837 already fed the typed violations back for a bounded
                // re-ask, and this is what is left once that budget is spent. Raw, it left the arm as an engine
                // exception and RED the blessed wire on the first unlucky sample; returned, it is one best-of-N attempt.
                return (false, $"the live planner's reply never bound to the typed acceptance contract, even after the provider's bounded re-ask — {ex.GetType().Name}: {ex.Message}");
            }

            plan.AuthoredByModel.ShouldNotBeNullOrWhiteSpace("the response must come from the pinned real provider path");
            plan.Subtasks.Count.ShouldBe(2);
            plan.Subtasks.ShouldAllBe(item => item.Acceptance != null);
            var command = plan.Subtasks.Single(item => item.Acceptance!.Kind == BenchmarkGradingKind.TestsPass).Acceptance!;
            var files = plan.Subtasks.Single(item => item.Acceptance!.Kind == BenchmarkGradingKind.ArtifactPresent).Acceptance!;
            files.Command.ShouldBe(new[] { "report.txt" }, "the model must author literal file obligations, not test/-f argv in the path field");
            command.Command.Count.ShouldBeGreaterThan(0);

            var directory = Path.Combine(Path.GetTempPath(), $"cs-live-planner-grade-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var report = Path.Combine(directory, "report.txt");
                await File.WriteAllTextAsync(report, "accepted\n");
                var grader = scope.Resolve<ISupervisorAcceptanceGrader>();
                var correct = await grader.GradeDirectoryAsync(directory, command, teamId, 20, CancellationToken.None);
                correct.Passed.ShouldBeTrue(correct.Detail);
                correct.EvidenceArtifactId.ShouldNotBeNull("an actual command verdict carries persisted oracle evidence");
                (await grader.GradeDirectoryAsync(directory, files, teamId, 20, CancellationToken.None)).Passed.ShouldBeTrue();

                await File.WriteAllTextAsync(report, "incorrect\n");
                var wrong = await grader.GradeDirectoryAsync(directory, command, teamId, 20, CancellationToken.None);
                wrong.Passed.ShouldBeFalse("the exact same model-authored command must reject the negative fixture");
                wrong.Class.ShouldBe(GradeFailureClass.Genuine);
                wrong.EvidenceArtifactId.ShouldNotBeNull();
                (await grader.GradeDirectoryAsync(directory, files, teamId, 20, CancellationToken.None)).Passed.ShouldBeTrue();
                File.Delete(report);
                (await grader.GradeDirectoryAsync(directory, files, teamId, 20, CancellationToken.None)).Passed.ShouldBeFalse();
            }
            finally { Directory.Delete(directory, recursive: true); }

            return (true, $"the live planner authored a {command.Command.Count}-token TestsPass argv that accepted the correct fixture and rejected the wrong one, plus an independent ArtifactPresent obligation on report.txt");
        });
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is the live planner's REPLY failing the typed acceptance contract, rather than
    /// infrastructure breaking underneath it. Two shapes, both meaning "the model could not author a conforming plan":
    /// the provider's bounded re-ask spent with the second reply still invalid (a Malformed
    /// <see cref="LlmApiException"/> — the categories the gate's own <c>IsGatewayInfraFailure</c> deliberately excludes
    /// from infra), and <c>LlmWorkflowPlanner.Deserialize</c>'s <see cref="InvalidOperationException"/> wrapper over a
    /// <see cref="JsonException"/> for a reply that is well-formed JSON yet binds to no <c>PlannedWorkflow</c>.
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
