using System.Reflection;
using System.Reflection.Emit;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

public sealed class AgentRunOwnerCallSiteTests
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(c => unchecked((ushort)c.Value));

    [Fact]
    public void Production_workers_cannot_call_legacy_run_id_only_writes_or_claims()
    {
        string[] legacy = ["MarkRunningAsync", "ReclaimForReattachAsync", "HeartbeatAsync", "SetRunnerHandleAsync", "SetSandboxConfinementAsync", "AppendEventAsync", "AppendEventsAsync", "CompleteAsync"];
        var violations = new List<string>();
        foreach (var type in typeof(AgentRunExecutor).Assembly.GetTypes())
        {
            if (type == typeof(AgentRunService) || type.FullName!.StartsWith(typeof(AgentRunService).FullName + "+", StringComparison.Ordinal)) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
            {
                foreach (var call in Calls(method))
                    if ((call.DeclaringType == typeof(IAgentRunService) || call.DeclaringType == typeof(AgentRunService)) && legacy.Contains(call.Name) && call.GetParameters().FirstOrDefault()?.ParameterType == typeof(Guid))
                        violations.Add($"{type.FullName}.{method.Name} calls {call}");
            }
        }
        violations.ShouldBeEmpty("worker authorization must be supplied by the invocation's owner token; legacy overloads exist only for rows with no ownership protocol");
    }

    [Fact]
    public void Legacy_reattach_does_not_resolve_any_persisted_run_or_current_owner()
    {
        var legacy = typeof(AgentRunExecutor).GetMethod(nameof(AgentRunExecutor.ReattachAsync), [typeof(Guid), typeof(CancellationToken)])!;
        Calls(legacy).ShouldNotContain(c => c.DeclaringType == typeof(IAgentRunService) || c.Name.Contains("Reattach", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Heartbeat_loss_cancels_observation_but_a_transient_database_failure_does_not(bool lost)
    {
        var owner = new AgentRunOwnerToken(Guid.NewGuid(), Guid.NewGuid(), 7);
        var service = DispatchProxy.Create<IAgentRunService, HeartbeatProxy>();
        var proxy = (HeartbeatProxy)service;
        proxy.Owner = owner;
        proxy.Failure = lost ? new AgentRunOwnershipLostException(owner.RunId) : new IOException("temporary database failure");
        using var observer = new CancellationTokenSource();
        var failure = await Record.ExceptionAsync(() => AgentRunExecutor.RenewObservationAsync(service, owner, observer, CancellationToken.None));
        failure.ShouldBeSameAs(proxy.Failure);
        observer.IsCancellationRequested.ShouldBe(lost);
    }

    public class HeartbeatProxy : DispatchProxy
    {
        public AgentRunOwnerToken Owner { get; set; } = null!;
        public Exception Failure { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            method!.Name.ShouldBe(nameof(IAgentRunService.HeartbeatAsync));
            args![0].ShouldBe(Owner);
            return Task.FromException(Failure);
        }
    }

    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) yield break;
        for (var offset = 0; offset < bytes.Length;)
        {
            var first = bytes[offset++];
            var code = Codes[first == 0xfe ? (ushort)(0xfe00 | bytes[offset++]) : first];
            if (code.OperandType == OperandType.InlineMethod)
            {
                var called = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset), method.DeclaringType?.GetGenericArguments(), method is MethodInfo methodInfo ? methodInfo.GetGenericArguments() : Type.EmptyTypes);
                if (called != null) yield return called;
            }
            offset += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4,
            };
        }
    }
}
