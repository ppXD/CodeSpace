using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.UnitTests.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// 🟢 Unit: an acceptance argv authored with <c>{{team.X}}</c> runs with the exact value, and nothing the grade keeps
/// holds it. The evidence repeats the argv (<c>$ sh -c …</c>) and both output tails, then becomes a team artifact a
/// Viewer can download and a tail on the agent's result. The real <see cref="SupervisorAcceptanceGrader"/> and
/// <see cref="TestsPassGrader"/> run against a recording runner, so the argv the check ran and the bytes the store
/// received are both observed.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AcceptanceEvidenceSecretMaskingTests
{
    private const string Secret = "CANARY-grade-5d3c0f81e2";

    [Theory]
    [InlineData(SandboxStatus.Success)]
    [InlineData(SandboxStatus.Failed)]
    public async Task The_check_runs_with_the_exact_secret_and_its_evidence_keeps_only_the_mask(SandboxStatus checkStatus)
    {
        var runner = new RecordingRunner { Check = new SandboxResult { Status = checkStatus, ExitCode = checkStatus == SandboxStatus.Success ? 0 : 1, Stdout = $"sent Authorization: Bearer {Secret}", Stderr = $"401 for {Secret}" } };
        var artifacts = new SupervisorAcceptanceGraderTests.FakeArtifactStore();

        var grade = await GradeAsync(runner, artifacts, new[] { "sh", "-c", $"curl -H 'Authorization: Bearer {Secret}' https://deploy.test" }, setup: null, secrets: [Secret]);

        string.Join(' ', runner.Specs.Last().Args).ShouldContain(Secret, customMessage: "the check runs with the EXACT argv — masking may never corrupt a value the platform itself needs");
        grade.Passed.ShouldBe(checkStatus == SandboxStatus.Success);

        var evidence = artifacts.Puts.ShouldHaveSingleItem("both verdicts store their evidence").Text;
        evidence.ShouldContain($"Bearer {PersistenceSecretRedactor.Marker}", customMessage: "the argv line and the output tails survive, masked");
        evidence.ShouldNotContain(Secret);
        (grade.EvidenceTail ?? "").ShouldNotContain(Secret);
        (grade.OracleNote ?? "").ShouldNotContain(Secret);
        grade.Detail.ShouldNotContain(Secret);
    }

    [Fact]
    public async Task A_failed_setup_keeps_its_detail_class_with_the_echoed_secret_masked()
    {
        // A failed setup's detail carries its stderr, and the detail lands on the result and its error line. Its
        // prefix is what the infra classifier reads, so the mask must leave it intact.
        var runner = new RecordingRunner { Setup = new SandboxResult { Status = SandboxStatus.Failed, ExitCode = 1, Stdout = "", Stderr = $"npm ERR! 401 token {Secret} rejected" } };

        var grade = await GradeAsync(runner, new SupervisorAcceptanceGraderTests.FakeArtifactStore(), new[] { "npm", "test" }, setup: new[] { "sh", "-c", $"NPM_TOKEN={Secret} npm ci" }, secrets: [Secret]);

        grade.Detail.ShouldBe($"setup-failed: npm ERR! 401 token {PersistenceSecretRedactor.Marker} rejected");
        grade.Class.ShouldBe(GradeFailureClass.Environment);
    }

    [Fact]
    public async Task A_grade_for_a_task_embedding_no_secret_keeps_its_evidence_verbatim()
    {
        var runner = new RecordingRunner { Check = new SandboxResult { Status = SandboxStatus.Failed, ExitCode = 1, Stdout = $"echo {Secret}", Stderr = "" } };
        var artifacts = new SupervisorAcceptanceGraderTests.FakeArtifactStore();

        await GradeAsync(runner, artifacts, new[] { "sh", "-c", $"echo {Secret}; exit 1" }, setup: null, secrets: null);

        artifacts.Puts.ShouldHaveSingleItem().Text.ShouldContain(Secret, customMessage: "a value nobody named as a secret is not masked — the grade is byte-identical to before");
    }

    [Fact]
    public void Every_kept_field_of_a_grade_is_masked_and_a_grade_with_no_secrets_is_returned_as_is()
    {
        var grade = new BenchmarkGrade { Passed = false, Detail = $"setup-failed: {Secret}", EvidenceText = $"$ sh -c {Secret}", EvidenceTail = $"tail {Secret}", OracleNote = $"note {Secret}" };

        var masked = SupervisorAcceptanceGrader.MaskedGrade(grade, [Secret]);

        masked.Detail.ShouldBe("setup-failed: [REDACTED]");
        masked.EvidenceText.ShouldBe("$ sh -c [REDACTED]");
        masked.EvidenceTail.ShouldBe("tail [REDACTED]");
        masked.OracleNote.ShouldBe("note [REDACTED]");
        SupervisorAcceptanceGrader.MaskedGrade(grade, null).ShouldBeSameAs(grade);
    }

    [Theory]
    [InlineData("repository", "clone-failed: ")]
    [InlineData("patch", "clone-failed: ")]
    [InlineData("base", "clone-failed: ")]
    [InlineData("repository-fault", "grade-error: ")]
    public async Task A_grade_that_fails_before_its_check_runs_keeps_its_prefix_with_the_secret_masked(string lane, string prefix)
    {
        // The fail-closed arms quote the failure they caught, and that detail is kept: on the result, the supervisor's
        // decision tape, the Room. Its prefix is what the infra classifier reads, so only the secret may change.
        // MUTATION this pins: return a lane's grade without MaskedGrade (the shape before this fix, where only the
        // evidence capture masked) → the canary survives in the detail → red.
        var failure = $"fatal: could not read from https://ci:{Secret}@git.test/team/repo";
        var grader = new SupervisorAcceptanceGrader(new FailingResolver(lane.EndsWith("-fault", StringComparison.Ordinal) ? new InvalidOperationException(failure) : new WorkspaceException(failure)), null!, new RecordingRunner(), new BenchmarkGraderRegistry(new IBenchmarkGrader[] { new TestsPassGrader() }), null!, new SupervisorAcceptanceGraderTests.FakeArtifactStore(), null!, NullLogger<SupervisorAcceptanceGrader>.Instance);
        var spec = new SupervisorAcceptanceSpec { Command = new[] { "make", "test" } };
        IReadOnlyList<string> secrets = [Secret];

        var grade = lane switch
        {
            "patch" => await grader.GradePatchAsync(new PatchAcceptanceGradeRequest { RepositoryId = Guid.NewGuid(), TeamId = Guid.NewGuid(), BaseSha = "abc123", Spec = spec, TimeoutSeconds = 30, Posture = null, WorkflowSecrets = secrets }, CancellationToken.None),
            "base" => await grader.GradeBaseAsync(new BaseAcceptanceGradeRequest { RepositoryId = Guid.NewGuid(), TeamId = Guid.NewGuid(), BaseSha = "abc123", Spec = spec, TimeoutSeconds = 30, Posture = null, WorkflowSecrets = secrets }, CancellationToken.None),
            _ => await grader.GradeAsync(new RepositoryAcceptanceGradeRequest { RepositoryId = Guid.NewGuid(), TeamId = Guid.NewGuid(), Branch = "agent/x", Spec = spec, TimeoutSeconds = 30, Posture = null, WorkflowSecrets = secrets }, CancellationToken.None),
        };

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldStartWith(prefix, Case.Sensitive, "the classifier reads the prefix; masking may not move it");
        grade.Detail.ShouldNotContain(Secret, Case.Sensitive, $"the {lane} lane kept the secret in its detail: {grade.Detail}");
        grade.Detail.ShouldContain(PersistenceSecretRedactor.Marker);
    }

    private static async Task<BenchmarkGrade> GradeAsync(RecordingRunner runner, SupervisorAcceptanceGraderTests.FakeArtifactStore artifacts, string[] command, string[]? setup, IReadOnlyList<string>? secrets)
    {
        var directory = Directory.CreateTempSubdirectory("cs-grade-mask-").FullName;
        try
        {
            // Only the seams GradeDirectoryAsync reaches are real or recorded: the runner, the oracle registry and the
            // evidence store.
            var grader = new SupervisorAcceptanceGrader(null!, null!, runner, new BenchmarkGraderRegistry(new IBenchmarkGrader[] { new TestsPassGrader() }), null!, artifacts, null!, NullLogger<SupervisorAcceptanceGrader>.Instance);
            var spec = new SupervisorAcceptanceSpec { Command = command, SetupCommand = setup };

            return await grader.GradeDirectoryAsync(new DirectoryAcceptanceGradeRequest { Directory = directory, Spec = spec, TeamId = Guid.NewGuid(), TimeoutSeconds = 30, Posture = null, WorkflowSecrets = secrets }, CancellationToken.None);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>A resolver whose every resolve fails the way a clone does (or with any other fault), quoting the failure.</summary>
    private sealed class FailingResolver(Exception failure) : IAgentWorkspaceResolver
    {
        public Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) => throw failure;

        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false, string? pinnedSha = null) => throw failure;
    }

    /// <summary>Records every spec it runs; a spec carrying the setup argv answers <see cref="Setup"/>, any other <see cref="Check"/>.</summary>
    private sealed class RecordingRunner : ISandboxRunnerRegistry, ISandboxRunner
    {
        public SandboxResult Setup { get; init; } = new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" };
        public SandboxResult Check { get; init; } = new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" };
        public List<SandboxSpec> Specs { get; } = new();
        public string Kind => SandboxKinds.Local;
        public IReadOnlyList<ISandboxRunner> All => new ISandboxRunner[] { this };
        public ISandboxRunner Resolve(string kind) => this;

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            return Task.FromResult(spec.Args.Any(arg => arg.Contains("npm ci", StringComparison.Ordinal)) ? Setup : Check);
        }
    }
}
