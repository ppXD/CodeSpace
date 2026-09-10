using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Core.Settings;
using CodeSpace.Core.Settings.Logging;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>Observes THIS host's runtime bundle so a campaign can freeze it and later refuse a substituted one.</summary>
public interface IQualificationRuntimeManifestObserver
{
    /// <summary>Freeze what this worker is right now. The credential endpoints and reviewer resolution are supplied by the caller, which owns the team-scoped rows and the decryption the fingerprint consumes.</summary>
    QualificationRuntimeManifest Observe(IReadOnlyList<CredentialEndpointIdentity> credentialEndpoints, ReviewerResolution reviewer);
}

/// <summary>
/// The single place that answers "what runtime is this". Every value is read from the SAME production seam the
/// executing path reads — the harness adapters' own command resolution, the bubblewrap probe, the bound
/// <see cref="RuntimeSettings"/>, the live planner/supervisor prompt and schema constants — so a frozen manifest and a
/// later observation can only differ when the runtime genuinely differed. Nothing here touches the database or the
/// network; the two inputs it cannot derive locally are parameters.
/// </summary>
public sealed class QualificationRuntimeManifestObserver : IQualificationRuntimeManifestObserver, ISingletonDependency
{
    private readonly IAgentHarnessRegistry _harnesses;

    public QualificationRuntimeManifestObserver(IAgentHarnessRegistry harnesses) => _harnesses = harnesses;

    public QualificationRuntimeManifest Observe(IReadOnlyList<CredentialEndpointIdentity> credentialEndpoints, ReviewerResolution reviewer) => new()
    {
        Harnesses = HarnessBinaryObserver.Observe(_harnesses.All),
        Runner = ObserveRunner(),
        CredentialEndpoints = credentialEndpoints.OrderBy(endpoint => endpoint.Role).ThenBy(endpoint => endpoint.ModelRowId).ToList(),
        Reviewer = reviewer,
        Execution = ObserveExecution(),
    };

    /// <summary>The judge-independence policy generation in force — pinned by a unit test, because a campaign frozen under one policy cannot be graded under another.</summary>
    public static ReviewerResolution ResolveReviewer(Guid? judgeModelRowId, Guid? criticModelRowId) => new()
    {
        JudgeModelRowId = judgeModelRowId,
        CriticModelRowId = criticModelRowId,
        IndependencePolicyVersion = LlmRubricJudge.EvaluatorGeneration,
    };

    private static RunnerProfile ObserveRunner() => new()
    {
        BuildIdentity = BuildIdentity.Value,
        OsPlatform = OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "OSX" : OperatingSystem.IsWindows() ? "Windows" : RuntimeInformation.RuntimeIdentifier,
        OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        BubblewrapAvailable = BubblewrapSandbox.Available is not null,
        RequireConfinement = RuntimeSettings.Current.RequireSandboxConfinement,
        MaxAutonomy = AgentAutonomyPolicy.DeploymentCeiling.ToString(),
    };

    private static ExecutionSettings ObserveExecution() => new()
    {
        ArmEffortTiers = ArmEffortTiers(),
        DefaultCompletionMode = CompletionPolicy.CurrentMode,
        CompletionPolicyVersion = CompletionPolicy.CurrentVersion,
        CellDriveGraceSeconds = TaskLaunch.TaskLaunchBenchmarkCellRunner.DriveGraceSeconds,
        DeepAgentTimeoutSeconds = Tasks.TaskLaunchService.DeepAgentTimeoutSeconds,
        LlmRequestTimeoutSeconds = (int)LlmHttpDefaults.RequestTimeout.TotalSeconds,
        AcceptanceEvaluatorVersion = SupervisorAcceptanceGrader.EvaluatorVersion,
        DeliveryEvaluatorVersion = CompletionAssessmentComposer.DeliveryEvaluatorVersion,
        PlannerPromptDigest = TextDigest(LlmWorkflowPlanner.SystemPrompt),
        PlannerSchemaDigest = TextDigest(ToolCallKey.Canonicalize(PlannerSchema.ResponseSchema)),
        SupervisorPromptDigest = TextDigest(LlmSupervisorDecider.SystemPrompt),
        SupervisorSchemaDigest = TextDigest(ToolCallKey.Canonicalize(SupervisorDecisionSchema.ResponseSchema)),
    };

    // Every TaskLaunch arm's requested effort, from the ONE mapping the cell runner reads. A remap changes what an
    // arm measured while every task definition — and therefore the suite digest — stays byte-identical.
    private static IReadOnlyDictionary<string, string?> ArmEffortTiers() =>
        Enum.GetValues<BenchmarkMode>().Where(BenchmarkModeEffort.IsTaskLaunch).OrderBy(mode => mode.ToString(), StringComparer.Ordinal)
            .ToDictionary(mode => mode.ToString(), BenchmarkModeEffort.RequestedEffortFor, StringComparer.Ordinal);

    private static string TextDigest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
