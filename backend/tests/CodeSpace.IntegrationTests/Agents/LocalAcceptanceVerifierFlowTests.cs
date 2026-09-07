using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LocalAcceptanceVerifierFlowTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("missing")]
    [InlineData("same-size")]
    [InlineData("inline")]
    public async Task A_declared_receipt_cannot_pass_after_its_stored_object_is_lost_or_corrupted(string damage)
    {
        using var seed = await SeedAsync(["report.txt"], kind: BenchmarkGradingKind.ArtifactPresent);
        var content = System.Security.Cryptography.RandomNumberGenerator.GetBytes(damage == "inline" ? 100 : 20_000);
        await File.WriteAllBytesAsync(Path.Combine(seed.Directory, "report.txt"), content);
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        (await scope.Resolve<IArtifactManifestStore>().CaptureDeclaredAsync(seed.Task, seed.Directory, seed.Owner.RunId, null, seed.TeamId, seed.Owner.Epoch, CancellationToken.None)).ShouldBe(1);
        var receipt = (await scope.Resolve<IArtifactManifestStore>().ListForAgentRunAsync(seed.Owner.RunId, seed.TeamId, CancellationToken.None)).ShouldHaveSingleItem();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var artifact = await db.WorkflowArtifact.AsNoTracking().SingleAsync(row => row.Id == receipt.ContentArtifactId);
        if (damage == "missing") File.Delete(new Uri(artifact.StorageUrl!).LocalPath);
        else if (damage == "same-size") await File.WriteAllBytesAsync(new Uri(artifact.StorageUrl!).LocalPath, new byte[content.Length]);
        else
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact DISABLE TRIGGER workflow_artifact_enforce_immutability");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_artifact SET inline_bytes = {new byte[content.Length]} WHERE id = {artifact.Id}");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact ENABLE TRIGGER workflow_artifact_enforce_immutability");
            await transaction.CommitAsync();
        }
        var grade = await verifier.GradeAsync(seed.Request(context), CancellationToken.None);
        grade.Passed.ShouldBeFalse("a correct workspace file and metadata cannot substitute for physically readable stored work");
        grade.Class.ShouldBe(GradeFailureClass.GraderFault);
        grade.Detail.ShouldStartWith("grade-error: declared-deliverable-");
    }

    [Fact]
    public async Task An_authority_denial_from_the_grader_callback_preserves_its_original_exception()
    {
        using var seed = await SeedAsync(["/bin/sh", "-c", "exit 0"]);
        var denied = new AgentAuthorityDeniedException("callback-revoked");
        using var scope = fixture.BeginScope(builder => builder.RegisterInstance(new DenyingGrader(denied)).As<ISupervisorAcceptanceGrader>());
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        (await Should.ThrowAsync<AgentAuthorityDeniedException>(() => verifier.GradeAsync(seed.Request(context), CancellationToken.None))).ShouldBeSameAs(denied);
    }

    [Theory]
    [InlineData("accepted", true)]
    [InlineData("incorrect", false)]
    public async Task Real_command_verdict_and_durable_evidence_preserve_exact_argv(string content, bool expected)
    {
        using var seed = await SeedAsync(["/bin/sh", "-c", "test \"$1\" = \"\" && test \"$2\" = \"  \" && test \"$(cat report.txt)\" = accepted", "oracle", "", "  "]);
        await File.WriteAllTextAsync(Path.Combine(seed.Directory, "report.txt"), content);
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        var grade = await verifier.GradeAsync(seed.Request(context), CancellationToken.None);
        grade.Passed.ShouldBe(expected);
        grade.Detail.ShouldBe(expected ? "tests-passed" : "tests-failed-exit-1");
        grade.Class.ShouldBe(expected ? null : GradeFailureClass.Genuine);
        grade.EvidenceArtifactId.ShouldNotBeNull();
        var bytes = await scope.Resolve<IArtifactStore>().GetBytesAsync(seed.TeamId, grade.EvidenceArtifactId.Value, CancellationToken.None);
        System.Text.Encoding.UTF8.GetString(bytes.ShouldNotBeNull().Bytes).ShouldContain(expected ? "exit=0" : "exit=1");
        (await scope.Resolve<IArtifactStore>().GetBytesAsync(Guid.NewGuid(), grade.EvidenceArtifactId.Value, CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_argv_and_blank_executable_are_typed_incomplete_contracts(bool blank)
    {
        using var seed = await SeedAsync(blank ? [" ", "true"] : []);
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        var grade = await verifier.GradeAsync(seed.Request(context), CancellationToken.None);
        grade.Passed.ShouldBeFalse();
        grade.Class.ShouldBe(GradeFailureClass.SpecIncomplete);
        grade.Detail.ShouldBe("grade-error: invalid-acceptance-argv");
        grade.EvidenceArtifactId.ShouldBeNull();
    }

    [Fact]
    public async Task A_context_cannot_be_reused_for_another_team_owner_or_contract()
    {
        using var seed = await SeedAsync(["/bin/sh", "-c", "touch should-not-run"]);
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        var requests = new[]
        {
            seed.Request(context) with { TeamId = Guid.NewGuid() },
            seed.Request(context) with { Owner = seed.Owner with { OwnerId = Guid.NewGuid() } },
            seed.Request(context) with { Task = seed.Task with { Acceptance = new SupervisorAcceptanceSpec { Command = ["/bin/true"] } } },
        };
        foreach (var request in requests)
        {
            var grade = await verifier.GradeAsync(request, CancellationToken.None);
            grade.Passed.ShouldBeFalse();
            grade.Class.ShouldBe(GradeFailureClass.GraderFault);
            grade.Detail.ShouldBe("grade-error: local-context-mismatch");
        }
        File.Exists(Path.Combine(seed.Directory, "should-not-run")).ShouldBeFalse();
    }

    [Fact]
    public async Task Fresh_revocation_and_expired_owner_prevent_oracle_execution()
    {
        using var seed = await SeedAsync(["/bin/sh", "-c", "touch should-not-run"]);
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        using (var mutation = fixture.BeginScope())
            await mutation.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 minute' WHERE id = {seed.Owner.RunId}");
        await Should.ThrowAsync<AgentRunOwnershipLostException>(() => verifier.GradeAsync(seed.Request(context), CancellationToken.None));
        using (var mutation = fixture.BeginScope())
        {
            var db = mutation.Resolve<CodeSpaceDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() + interval '1 hour' WHERE id = {seed.Owner.RunId}");
            await db.TeamMembership.Where(row => row.TeamId == seed.TeamId && row.UserId == seed.UserId).ExecuteDeleteAsync();
        }
        await Should.ThrowAsync<AgentAuthorityDeniedException>(() => verifier.GradeAsync(seed.Request(context), CancellationToken.None));
        File.Exists(Path.Combine(seed.Directory, "should-not-run")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("missing-directory")]
    [InlineData("remote-runner")]
    [InlineData("no-live-context")]
    public async Task An_unavailable_execution_world_is_unknown_and_cannot_pass(string reason)
    {
        using var seed = await SeedAsync(["/bin/true"]);
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        var preparation = reason == "remote-runner" ? seed.Preparation with { RunnerKind = "remote" } : seed.Preparation;
        if (reason == "missing-directory") Directory.Delete(seed.Directory);
        using var context = await verifier.PrepareAsync(preparation, CancellationToken.None);
        var grade = await verifier.GradeAsync(seed.Request(reason == "no-live-context" ? null : context), CancellationToken.None);
        grade.Passed.ShouldBeFalse();
        grade.Class.ShouldBe(GradeFailureClass.Environment);
        grade.Detail.ShouldStartWith("grade-error: ");
        grade.EvidenceArtifactId.ShouldBeNull();
    }

    [Fact]
    public async Task Cancellation_of_the_running_real_oracle_propagates_without_a_false_failure_or_retry()
    {
        using var seed = await SeedAsync(["/bin/sh", "-c", "echo started > started; sleep 20; touch finished"]);
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var pending = verifier.GradeAsync(seed.Request(context), cancellation.Token);
        var started = Path.Combine(seed.Directory, "started");
        for (var attempt = 0; attempt < 200 && !File.Exists(started) && !pending.IsCompleted; attempt++) await Task.Delay(25);
        File.Exists(started).ShouldBeTrue("the actual process must have reached its workspace before cancellation");
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        File.Exists(Path.Combine(seed.Directory, "finished")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_prepared_context_survives_service_scope_disposal_but_cannot_change_the_persisted_selected_directory()
    {
        using var seed = await SeedAsync(["/bin/sh", "-c", "exit 0"]);
        LocalAcceptanceContext context;
        using (var preparation = fixture.BeginScope()) context = await preparation.Resolve<LocalAcceptanceVerifier>().PrepareAsync(seed.Preparation, CancellationToken.None);
        using (context)
        using (var verification = fixture.BeginScope())
        {
            var verifier = verification.Resolve<LocalAcceptanceVerifier>();
            var original = await verifier.GradeAsync(seed.Request(context), CancellationToken.None);
            original.Passed.ShouldBeTrue(original.Detail);
            using var forged = await verifier.PrepareAsync(seed.Preparation with { Task = seed.Task with { WorkspaceDirectory = Path.GetTempPath() }, Directory = Path.GetTempPath() }, CancellationToken.None);
            var grade = await verifier.GradeAsync(seed.Request(forged), CancellationToken.None);
            grade.Passed.ShouldBeFalse();
            grade.Class.ShouldBe(GradeFailureClass.GraderFault);
        }
    }

    [Fact]
    public async Task A_legacy_git_pathspec_is_not_reinterpreted_as_a_protected_local_file()
    {
        using var seed = await SeedAsync(["/bin/true"], protectedPaths: ["*.sh"]);
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        var grade = await verifier.GradeAsync(seed.Request(context), CancellationToken.None);
        grade.Passed.ShouldBeFalse();
        grade.Class.ShouldBe(GradeFailureClass.SpecIncomplete);
        grade.Detail.ShouldBe("grade-error: local-protected-pathspec-unsupported");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_oracle_bytes_before_or_during_execution_invalidate_a_verdict(bool during)
    {
        using var seed = await SeedAsync(["/bin/sh", "judge.sh"], oraclePaths: ["judge.sh"]);
        var path = Path.Combine(seed.Directory, "judge.sh");
        await File.WriteAllTextAsync(path, during ? "printf 'exit 0\\n' > judge.sh; exit 0\n" : "exit 7\n");
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        if (!during) await File.WriteAllTextAsync(path, "exit 0\n");
        var grade = await verifier.GradeAsync(seed.Request(context), CancellationToken.None);
        grade.Passed.ShouldBeFalse();
        grade.Class.ShouldBe(GradeFailureClass.GraderFault);
        grade.Detail.ShouldBe("grade-error: oracle-integrity-changed");
        (await File.ReadAllTextAsync(path)).ShouldBe("exit 0\n");
    }

    [Fact]
    public async Task A_real_judge_that_exits_127_is_not_reclassified_as_a_missing_program()
    {
        using var seed = await SeedAsync(["/bin/sh", "-c", "printf 'judge actually ran'; exit 127"]);
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        var grade = await verifier.GradeAsync(seed.Request(context), CancellationToken.None);
        grade.Passed.ShouldBeFalse();
        grade.Class.ShouldBe(GradeFailureClass.Genuine);
        grade.Detail.ShouldBe("tests-failed-exit-127");
        grade.EvidenceTail.ShouldContain("judge actually ran");
    }

    [Fact]
    public async Task A_missing_program_never_mints_a_pass_and_preserves_the_available_launch_evidence()
    {
        using var seed = await SeedAsync(["/codespace-acceptance-program-that-does-not-exist"]);
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        var grade = await verifier.GradeAsync(seed.Request(context), CancellationToken.None);
        grade.Passed.ShouldBeFalse();
        // Direct spawning reports its actual exception. A bwrap exec failure returns an exit receipt whose 127
        // cannot distinguish it from a judge's own exit code; this slice does not manufacture that missing proof.
        if (grade.Class == GradeFailureClass.GraderFault)
        {
            grade.Detail.ShouldStartWith("grade-error: local-oracle-unavailable:");
            grade.EvidenceArtifactId.ShouldBeNull();
        }
        else
        {
            grade.Class.ShouldBe(GradeFailureClass.Genuine);
            grade.Detail.ShouldBe("tests-failed-exit-127");
            grade.EvidenceArtifactId.ShouldNotBeNull();
        }
    }

    [Theory]
    [InlineData("path")]
    [InlineData("epoch")]
    [InlineData("artifact-team")]
    public async Task The_same_receipt_count_cannot_cover_a_different_path_attempt_or_artifact_team(string mismatch)
    {
        using var seed = await SeedAsync(["report.txt"], kind: BenchmarkGradingKind.ArtifactPresent);
        await File.WriteAllTextAsync(Path.Combine(seed.Directory, "report.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(seed.Directory, "unrelated.txt"), "same");
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);
        var captureTask = mismatch == "path" ? seed.Task with { Acceptance = seed.Task.Acceptance! with { Command = ["unrelated.txt"] } } : seed.Task;
        var epoch = seed.Owner.Epoch + (mismatch == "epoch" ? 1 : 0);
        (await scope.Resolve<IArtifactManifestStore>().CaptureDeclaredAsync(captureTask, seed.Directory, seed.Owner.RunId, null, seed.TeamId, epoch, CancellationToken.None)).ShouldBe(1);
        if (mismatch == "artifact-team")
        {
            var (foreignTeam, _) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
            var foreignArtifact = await scope.Resolve<IArtifactStore>().PutAsync(foreignTeam, "same"u8.ToArray(), "text/plain", CancellationToken.None);
            await scope.Resolve<CodeSpaceDbContext>().ArtifactManifest.Where(row => row.AgentRunId == seed.Owner.RunId).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ContentArtifactId, foreignArtifact));
        }
        var grade = await verifier.GradeAsync(seed.Request(context), CancellationToken.None);
        grade.Passed.ShouldBeFalse();
        grade.Class.ShouldBe(GradeFailureClass.GraderFault);
        grade.Detail.ShouldBe(mismatch == "artifact-team" ? "grade-error: declared-deliverable-content-MetadataMissing" : "grade-error: declared-deliverable-receipt-missing");
    }

    private async Task<Seed> SeedAsync(IReadOnlyList<string> argv, IReadOnlyList<string>? oraclePaths = null, IReadOnlyList<string>? protectedPaths = null, BenchmarkGradingKind? kind = null)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var directory = Path.Combine(Path.GetTempPath(), "cs-local-grade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var task = new AgentTask { Goal = "verify exact local work", Harness = "test", WorkspaceDirectory = directory, Autonomy = AgentAutonomyLevel.Trusted, Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted), Acceptance = new SupervisorAcceptanceSpec { Kind = kind, Command = argv, OraclePaths = oraclePaths, ProtectedPaths = protectedPaths, TimeoutSeconds = 30 } };
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var runs = scope.Resolve<IAgentRunService>();
        var run = await runs.CreateAsync(task, teamId, null, null, cancellationToken: CancellationToken.None);
        var owner = await runs.ClaimOwnershipAsync(run.Id, CancellationToken.None);
        return new Seed(teamId, userId, directory, task, owner.ShouldNotBeNull());
    }

    private sealed record Seed(Guid TeamId, Guid UserId, string Directory, AgentTask Task, AgentRunOwnerToken Owner) : IDisposable
    {
        public LocalAcceptancePreparation Preparation => new(Owner, TeamId, Task, SandboxKinds.Local, Directory);
        public LocalAcceptanceRequest Request(LocalAcceptanceContext? context) => new(Owner, TeamId, Task, context);
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }

    // Fault-injection seam only: real authority/admission and workspace preparation still execute above.
    private sealed class DenyingGrader(AgentAuthorityDeniedException denied) : ISupervisorAcceptanceGrader
    {
        public Task<BenchmarkGrade> GradeDirectoryAsync(string directory, SupervisorAcceptanceSpec spec, Guid teamId, int timeoutSeconds, CancellationToken cancellationToken) => throw denied;
        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BenchmarkGrade> GradeBaseAsync(Guid repositoryId, Guid teamId, string baseSha, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
