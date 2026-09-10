using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Llm;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.RoutePreview;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Fits the existing RealModelPlanner CI filter: real classifier wire, production preview/launch and Postgres. Jobs
/// remain undispatched; this measures routing reuse, not task execution quality.
///
/// <para>A classifier fallback whose <see cref="EffortDecision.FallbackReason"/> classifies as gateway infra (via
/// <see cref="RealModelGate.IsGatewayInfraCategory"/>) is a non-gating skip — nothing was measured about real routing,
/// the same as any other gateway outage on this lane. A fallback with an unclassified reason (or none recorded) is a
/// real regression and gates.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
public sealed class RealModelPlannerRouteSnapshotFlowTests
{
    private readonly PostgresFixture _fixture;

    public RealModelPlannerRouteSnapshotFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [SkippableTheory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_real_classified_preview_is_the_decision_that_launch_and_retry_consume(string provider)
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);
        if (baseUrl is null || apiKey is null || model is null) throw RealModelGate.ReportSkipped(provider, "CODESPACE_LLM_* absent; live routing was not measured");

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        using var rootScope = _fixture.BeginScope();
        var jobs = rootScope.Resolve<InMemoryBackgroundJobClient>();
        jobs.AutoExecute = false;
        var classifier = new CountingClassifier(new LlmEffortClassifier(RealModelLiveWire.Registry(), RealModelLiveWire.Selector(model, RealModelLiveWire.Credential(provider, baseUrl, apiKey)), rootScope.Resolve<ITaskRecipeRegistry>(), new HeuristicEffortClassifier()));
        using var scope = _fixture.BeginScope(b => b.RegisterInstance(new EffortClassifierRegistry([new HeuristicEffortClassifier(), classifier])).As<IEffortClassifierRegistry>());
        var input = new TaskLaunchRequest { TeamId = teamId, ActorUserId = userId, SurfaceKind = "chat", TaskText = "Write a concise explanation of how a durable queue recovers an interrupted job", Autonomy = "Confined", AcceptanceCriteria = ["Explain retries and duplicate suppression"] };

        var preview = await scope.Resolve<ITaskRoutePreviewService>().PreviewAsync(input, CancellationToken.None);
        var decision = preview.Route.Decision!;

        // A fallback to the heuristic classifier whose reason classifies as gateway infra (the LLM effort classifier's
        // model call hit a transient/rate-limited/auth gateway fault, per LlmEffortClassifier.ClassifyFallbackReason) is
        // NOTHING measured about real routing — a non-gating skip, not the behavioural miss a heuristic fallback with an
        // unclassified (or no) reason still is.
        if (decision.ClassifierKind != LlmEffortClassifier.ClassifierKind && decision.FallbackReason is { } reason && RealModelGate.IsGatewayInfraCategory(reason))
            throw new SkipException(RealModelGate.ReportInfraSkip(provider, new TimeoutException($"the structured-LLM effort classifier fell back to '{decision.ClassifierKind}' on a gateway-infra fault ({reason})"), Environment.GetEnvironmentVariable(RealModelGate.StepSummaryEnvVar)));

        decision.ClassifierKind.ShouldBe(LlmEffortClassifier.ClassifierKind, $"heuristic fallback is not a real-model success{(decision.FallbackReason is { } r ? $" (fallback reason '{r}' is NOT a gateway-infra signature — a real regression)" : "")}");
        classifier.Calls.ShouldBe(1);
        var launch = await scope.Resolve<ITaskLaunchService>().LaunchAsync(input with { RouteSnapshotId = preview.RouteSnapshotId }, CancellationToken.None);
        var retry = await scope.Resolve<ITaskLaunchService>().LaunchAsync(input with { RouteSnapshotId = preview.RouteSnapshotId }, CancellationToken.None);
        classifier.Calls.ShouldBe(1, "the model decision must not be sampled again when launch consumes the preview");
        JsonSerializer.Serialize(launch.Route, WorkflowJson.Options).ShouldBe(JsonSerializer.Serialize(preview.Route, WorkflowJson.Options));
        retry.RunId.ShouldBe(launch.RunId);
        retry.SessionId.ShouldBe(launch.SessionId);
        using var read = _fixture.BeginScope();
        (await read.Resolve<CodeSpaceDbContext>().WorkflowRun.CountAsync(r => r.TeamId == teamId)).ShouldBe(1);
        (await read.Resolve<CodeSpaceDbContext>().TaskRouteSnapshot.SingleAsync(s => s.Id == preview.RouteSnapshotId)).ConsumedRunId.ShouldBe(launch.RunId);
    }

    private sealed class CountingClassifier(IEffortClassifier inner) : IEffortClassifier
    {
        public string Kind => inner.Kind;
        public int Calls { get; private set; }
        public Task<EffortDecision> ClassifyAsync(EffortRouteRequest request, CancellationToken ct)
        {
            Calls++;
            return inner.ClassifyAsync(request, ct);
        }
    }
}
