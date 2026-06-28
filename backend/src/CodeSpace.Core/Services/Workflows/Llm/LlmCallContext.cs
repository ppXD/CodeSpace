using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// The ambient run-correlation for an in-process model call — a scoped caller (e.g. the supervisor turn) PUSHES one
/// around a model call so the singleton <see cref="RecordingStructuredLLMClientDecorator"/> can (a) bind its
/// <c>interaction.*</c> rows to the right run / node / turn and (b) reach the SCOPED ledger writer + artifact
/// offloader — a singleton decorator can't inject scoped collaborators, so they ride the scope. Absent (no caller
/// pushed one — a model call outside any run) ⇒ the decorator records nothing (FAIL-OPEN). Flows across awaits via
/// <see cref="AsyncLocal{T}"/>; the prior value is restored on dispose so nested / sequential calls don't leak.
/// </summary>
public sealed record LlmCallScope(
    Guid RunId,
    Guid TeamId,
    string? NodeId,
    string IterationKey,
    /// <summary>The OPEN step identifier stamped on the payload (e.g. "supervisor.decision", "planner.plan") — never switched on.</summary>
    string Kind,
    IRunRecordLogger Logger,
    IArtifactOffloader Offloader);

public static class LlmCallContext
{
    private static readonly AsyncLocal<LlmCallScope?> Holder = new();

    /// <summary>The model-call scope in flight on this async path, or null when no run-scoped caller pushed one.</summary>
    public static LlmCallScope? Current => Holder.Value;

    /// <summary>Push a scope for the duration of the using-block; restores the prior value on dispose (re-entrant safe).</summary>
    public static IDisposable Push(LlmCallScope scope)
    {
        var prior = Holder.Value;
        Holder.Value = scope;
        return new Scope(prior);
    }

    private sealed class Scope : IDisposable
    {
        private readonly LlmCallScope? _prior;
        private bool _disposed;

        public Scope(LlmCallScope? prior) { _prior = prior; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Holder.Value = _prior;
        }
    }
}
