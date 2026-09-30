using System.Text.Json;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit (the real <see cref="AgentSupervisorNode"/> over a turn service whose turn faults): the supervisor's own
/// model-plane park writes the SAME redacted, clamped fault text the generic <see cref="InfraPark"/> does — on its
/// marker AND in its park log line. The supervisor is the one lane that parks through its own code, and it stored the
/// raw <c>fault.Message</c> (the provider's whole error body, unredacted and unbounded) while logging no fault text.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentSupervisorNodeInfraParkTests
{
    /// <summary>A value the run treats as secret — a team variable, listed on <c>SecretPaths</c> under the engine's own spelling (<c>&lt;bucket&gt;.&lt;variable&gt;</c>).</summary>
    private const string GatewayToken = "gw-tok-7f3a9c2e41d8";

    [Fact]
    public async Task The_supervisors_park_writes_the_redacted_clamped_fault_text_on_its_marker_and_in_its_log()
    {
        var fault = new LlmApiException("Anthropic", 500, LlmErrorCategory.Transient, $"gateway rejected bearer {GatewayToken}: " + new string('x', 5_000));
        fault.Message.ShouldContain(GatewayToken, Case.Sensitive, "fixture check: the raw fault carries the secret, or the redaction asserted below proves nothing");
        var logger = new CapturingLogger();

        var result = await new AgentSupervisorNode(ScopeFactoryOver(new FaultingTurns(fault))).RunAsync(ContextHoldingSecret(logger), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended, "an exhausted transient fault parks the supervisor — it must never terminalize the run");
        result.SuspendUntil!.Kind.ShouldBe(WorkflowWaitKinds.SupervisorInfraPark);

        var marker = result.SuspendUntil.Payload.GetProperty("error").GetString()!;
        marker.ShouldNotContain(GatewayToken, Case.Sensitive, "the marker is durable and shown on the run detail while the supervisor is parked");
        marker.ShouldContain(PersistenceSecretRedactor.Marker, Case.Sensitive);
        marker.Length.ShouldBe(513, "512 characters of the fault, then the ellipsis");
        marker.ShouldEndWith("…", Case.Sensitive);

        logger.Warnings.ShouldHaveSingleItem().ShouldEndWith(marker, Case.Sensitive, "the park line carries the fault text — the SAME text the marker stores, redacted and clamped alike");
    }

    private static IServiceScopeFactory ScopeFactoryOver(ISupervisorTurnService turns) => new ServiceCollection().AddSingleton(turns).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private static NodeRunContext ContextHoldingSecret(ILogger logger) => new()
    {
        Inputs = new Dictionary<string, JsonElement>(),
        Config = new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope
        {
            Trigger = new Dictionary<string, JsonElement>(),
            Sys = new Dictionary<string, JsonElement>
            {
                [SystemScopeKeys.WorkflowRunId] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
                [SystemScopeKeys.TeamId] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
            },
            Team = new Dictionary<string, JsonElement> { ["GATEWAY_TOKEN"] = JsonSerializer.SerializeToElement(GatewayToken) },
            SecretPaths = new HashSet<string> { "team.GATEWAY_TOKEN" },
        },
        Logger = logger,
        Observability = NodeObservability.NoOp,
        NodeId = "supervisor",
    };

    /// <summary>A turn service with nothing pending whose turn faults — the state a supervisor re-enters into while the model plane is down.</summary>
    private sealed class FaultingTurns(LlmApiException fault) : ISupervisorTurnService
    {
        public Task<SupervisorTurnContext> RehydrateFromDecisionLogAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, SupervisorGoalConfig? goalConfig, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SupervisorTurnResult> RunTurnAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, Guid? conversationId, SupervisorGoalConfig? goalConfig, CancellationToken cancellationToken) => Task.FromException<SupervisorTurnResult>(fault);
        public Task<int> CountPendingAgentWaitsAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<string?> PendingHumanWaitTokenAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<bool> ReopenDiscardedAskAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<SupervisorTurnResult> ForceStopAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, SupervisorGoalConfig? goalConfig, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> SupervisorDepthAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Keeps every warning the node writes, formatted the way a sink would render it.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception)); }

        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
