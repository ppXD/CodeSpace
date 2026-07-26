using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Fixture-singleton dial for <see cref="FaultingLlmClient"/>. A test sets how many consecutive model calls fault
/// and with which category, then resets in a <c>finally</c> — the client itself stays stateless about intent.
/// </summary>
public sealed class LlmFaultScript
{
    private int _remaining;

    /// <summary>The category the next faults carry. Transient and RateLimited park; everything else must fail the node.</summary>
    public LlmErrorCategory Category { get; private set; } = LlmErrorCategory.Transient;

    /// <summary>How many faults were actually thrown — a test asserts on this so a fake that silently stopped faulting cannot pass as a park that recovered.</summary>
    public int Thrown { get; private set; }

    public void FaultNext(int calls, LlmErrorCategory category = LlmErrorCategory.Transient)
    {
        _remaining = calls;
        Category = category;
        Thrown = 0;
    }

    /// <summary>Fault every call until reset — for the ladder test, where the outage never lifts.</summary>
    public void FaultAlways(LlmErrorCategory category = LlmErrorCategory.Transient) => FaultNext(int.MaxValue, category);

    public void Reset() => FaultNext(0);

    internal bool TryConsume()
    {
        if (_remaining <= 0) return false;

        if (_remaining != int.MaxValue) _remaining--;

        Thrown++;
        return true;
    }
}

/// <summary>
/// An <see cref="ILLMClient"/> that throws a real <see cref="LlmApiException"/> on demand, so a flow test can drive a
/// MODEL-PLANE OUTAGE through the real engine instead of asserting on a helper in isolation.
///
/// <para>It exists because nothing else in the suite could produce this fault at this seam: the only other injected
/// <c>LlmApiException</c> lives in <c>ScriptedSupervisorDecider</c>, one layer up at <c>ISupervisorDecider</c>, which
/// a <c>llm.complete</c> node never touches.</para>
///
/// <para>Deliberately implements ONLY <see cref="ILLMClient"/> (not the structured or streaming interfaces): a node
/// configured with default <c>maxTokens</c> takes the buffered path, which is the one worth fault-injecting, and the
/// narrow interface is what makes Autofac wrap it in the same recording decorator production uses — so the test also
/// proves the exception survives that decorator rather than assuming it.</para>
/// </summary>
public sealed class FaultingLlmClient : ILLMClient
{
    /// <summary>Its own provider tag, so the registry holds it beside the other fakes with no duplicate-provider collision.</summary>
    public const string ProviderTag = "TestFaulting";

    private readonly LlmFaultScript _script;

    public FaultingLlmClient(LlmFaultScript script) { _script = script; }

    public string Provider => ProviderTag;

    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken)
    {
        if (_script.TryConsume())
            throw new LlmApiException(ProviderTag, 503, _script.Category, $"injected {_script.Category} model-plane fault");

        return Task.FromResult(new LLMCompletion { Text = Recovered, Model = request.Model, Usage = new() { InputTokens = 11, OutputTokens = 13, FinishReason = "stop" } });
    }

    /// <summary>The text a non-faulting call returns — the marker proving the node RE-RAN its model call after the wake, rather than the engine replaying a cached result.</summary>
    public const string Recovered = "the outage lifted and this call went through";
}
