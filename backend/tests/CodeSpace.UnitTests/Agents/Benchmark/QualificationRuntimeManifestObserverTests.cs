using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// What the observer reads must be the SAME production seam the executing path reads, or a frozen manifest and a later
/// observation could differ while the runtime did not. These pin the deployment settings it echoes, the arm coverage,
/// the evaluator and policy generations it names, and that two observations of one unchanged host agree.
/// </summary>
[Trait("Category", "Unit")]
public sealed class QualificationRuntimeManifestObserverTests
{
    [Fact]
    public void Two_observations_of_one_unchanged_host_report_no_drift()
    {
        var observer = Observer();

        QualificationRuntimeManifest.Compare(observer.Observe(Endpoints(), Reviewer()), observer.Observe(Endpoints(), Reviewer()))
            .ShouldBeNull("a stable host must not manufacture drift, or every resumed campaign would be refused");
    }

    [Fact]
    public void The_runner_profile_echoes_this_deployments_confinement_and_autonomy_settings()
    {
        using var overridden = RuntimeSettings.Override(settings => settings with { RequireSandboxConfinement = true, MaxAutonomy = nameof(AgentAutonomyLevel.Confined) });

        var runner = Observer().Observe(Endpoints(), Reviewer()).Runner;

        runner.RequireConfinement.ShouldBeTrue();
        runner.MaxAutonomy.ShouldBe(nameof(AgentAutonomyLevel.Confined));
        runner.BubblewrapAvailable.ShouldBe(BubblewrapSandboxAvailable());
        runner.BuildIdentity.ShouldNotBeNullOrWhiteSpace();
        runner.OsArchitecture.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Every_task_launch_arm_contributes_its_requested_effort_tier()
    {
        var tiers = Observer().Observe(Endpoints(), Reviewer()).Execution.ArmEffortTiers;

        tiers.Keys.OrderBy(key => key, StringComparer.Ordinal)
            .ShouldBe(Enum.GetValues<BenchmarkMode>().Where(BenchmarkModeEffort.IsTaskLaunch).Select(mode => mode.ToString()).OrderBy(key => key, StringComparer.Ordinal));
        tiers[nameof(BenchmarkMode.TaskLaunchDeep)].ShouldBe("deep");
        tiers[nameof(BenchmarkMode.TaskLaunchAuto)].ShouldBeNull("the auto arm asks the router to classify rather than naming a tier");
    }

    [Fact]
    public void The_execution_settings_name_the_generations_actually_in_force()
    {
        var execution = Observer().Observe(Endpoints(), Reviewer()).Execution;

        execution.AcceptanceEvaluatorVersion.ShouldBe(SupervisorAcceptanceGrader.EvaluatorVersion);
        execution.DeliveryEvaluatorVersion.ShouldBe(CompletionAssessmentComposer.DeliveryEvaluatorVersion);
        execution.DefaultCompletionMode.ShouldBe(CompletionPolicy.CurrentMode);
        execution.CompletionPolicyVersion.ShouldBe(CompletionPolicy.CurrentVersion);
        execution.LlmRequestTimeoutSeconds.ShouldBeGreaterThan(0);
        new[] { execution.PlannerPromptDigest, execution.PlannerSchemaDigest, execution.SupervisorPromptDigest, execution.SupervisorSchemaDigest }
            .ShouldAllBe(digest => digest.Length == 64);
        execution.PlannerPromptDigest.ShouldNotBe(execution.SupervisorPromptDigest, "two different prompts must not digest alike");
        execution.PlannerSchemaDigest.ShouldNotBe(execution.SupervisorSchemaDigest);
    }

    [Fact]
    public void The_reviewer_resolution_pins_the_independence_policy_generation()
    {
        // A campaign frozen under one judge-independence policy cannot be graded under another, so a rename here is a
        // decision (Rule 8), not a refactor: the frozen manifest of every stored campaign names this exact string.
        LlmRubricJudge.EvaluatorGeneration.ShouldBe("llm-rubric-judge/v2-observed-identity");

        QualificationRuntimeManifestObserver.ResolveReviewer(Row(7), Row(8)).ShouldBe(new ReviewerResolution
        {
            JudgeModelRowId = Row(7),
            CriticModelRowId = Row(8),
            IndependencePolicyVersion = LlmRubricJudge.EvaluatorGeneration,
        });
    }

    [Fact]
    public void Credential_endpoints_are_ordered_by_arm_so_enumeration_order_cannot_move_the_digest()
    {
        var reversed = Endpoints().Reverse().ToList();

        Observer().Observe(reversed, Reviewer()).CredentialEndpoints.Select(endpoint => endpoint.Role)
            .ShouldBe(new[] { QualificationCredentialRole.Control, QualificationCredentialRole.Candidate, QualificationCredentialRole.Reviewer });
    }

    private static bool BubblewrapSandboxAvailable() => Core.Services.Agents.Sandbox.Isolation.BubblewrapSandbox.Available is not null;

    private static QualificationRuntimeManifestObserver Observer() => new(new AgentHarnessRegistry(new IAgentHarness[] { new StubHarness("stub-harness") }));

    private static ReviewerResolution Reviewer() => QualificationRuntimeManifestObserver.ResolveReviewer(Row(3), null);

    private static IReadOnlyList<CredentialEndpointIdentity> Endpoints() =>
    [
        Endpoint(QualificationCredentialRole.Control, 1),
        Endpoint(QualificationCredentialRole.Candidate, 2),
        Endpoint(QualificationCredentialRole.Reviewer, 3),
    ];

    private static CredentialEndpointIdentity Endpoint(QualificationCredentialRole role, int seed) =>
        CredentialEndpointIdentity.Observe(role, Row(seed), Row(seed + 10), "Anthropic", "https://gateway.example.com/v1", "secret", "campaign-salt");

    private static Guid Row(int seed) => Guid.Parse($"{seed:D8}-0000-0000-0000-000000000000");

    private sealed class StubHarness : IAgentHarness
    {
        public StubHarness(string kind) => Kind = kind;

        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "m" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "x" };
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();
        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) => new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed" });
    }
}
