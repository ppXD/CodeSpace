using Autofac;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// A pass-through <see cref="ILLMClient"/> that reports the PROVIDER-REPORTED model of every live response to
/// <see cref="RealModelGate.ObserveModel"/> — the ONE observing wrapper both real-model construction paths use (the
/// hand-built live-wire registry via <see cref="ModelObserving.Wrap"/>, and the DI-resolved client via the
/// <c>PostgresFixture</c> decorator registration). Without it a gate verdict can only name the model id it ASKED for —
/// a repository secret, masked in the CI log — so a gateway that quietly answers with a DIFFERENT model is
/// indistinguishable from a capability regression. Test-side only: production wiring is untouched, and
/// <see cref="RealModelGate.ObserveModel"/> is a no-op whenever no gate has armed a sink, so the decorator is inert for
/// every deterministic-fake lane it also wraps.
///
/// <para>This base decorates the NARROW plain-text face and is applied ONLY to a client that does NOT also implement
/// <see cref="IStructuredLLMClient"/>, so the decorated type mirrors the inner's exactly — the same three-way
/// conditional split (and for the same reason) as the production
/// <see cref="RecordingLLMClientDecorator"/> family: a consumer that feature-detects with
/// <c>is not IStructuredLLMClient</c> must not be fooled by a wrapper claiming a face its inner lacks.</para>
/// </summary>
public class ModelObservingLLMClientDecorator : ILLMClient
{
    private readonly ILLMClient _inner;

    public ModelObservingLLMClientDecorator(ILLMClient inner) { _inner = inner; }

    public string Provider => _inner.Provider;

    public async Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var completion = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        RealModelGate.ObserveModel(completion.Model);

        return completion;
    }
}

/// <summary>The structured-capable sibling — the face the supervisor decider reaches (<c>registry.All.OfType&lt;IStructuredLLMClient&gt;()</c>), so a live DECISION call is what actually feeds the sink.</summary>
public class ModelObservingStructuredLLMClientDecorator : ModelObservingLLMClientDecorator, IStructuredLLMClient
{
    private readonly IStructuredLLMClient _structuredInner;

    public ModelObservingStructuredLLMClientDecorator(ILLMClient inner) : base(inner) => _structuredInner = (IStructuredLLMClient)inner;

    public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var completion = await _structuredInner.CompleteStructuredAsync(request, cancellationToken).ConfigureAwait(false);
        RealModelGate.ObserveModel(completion.Model);

        return completion;
    }
}

/// <summary>The structured+streaming sibling. It TEES the event sequence: each event flows to the caller verbatim while a <see cref="LlmStreamEvent.Meta"/> naming a model feeds the sink — the streamed wire is where gateway multiplexing was first caught answering with another model, so it must be observed, not passed through blind.</summary>
public sealed class ModelObservingStreamingStructuredLLMClientDecorator : ModelObservingStructuredLLMClientDecorator, IStreamingLLMClient
{
    private readonly IStreamingLLMClient _streamingInner;

    public ModelObservingStreamingStructuredLLMClientDecorator(ILLMClient inner) : base(inner) => _streamingInner = (IStreamingLLMClient)inner;

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in _streamingInner.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (evt is LlmStreamEvent.Meta { Model: { } model }) RealModelGate.ObserveModel(model);

            yield return evt;
        }
    }
}

/// <summary>The two construction paths onto the SAME observing wrapper: <see cref="Wrap"/> for a hand-built registry (the live-wire gates), <see cref="RegisterDecorators"/> for a DI-resolved client (the whole-loop E2E). Both select by the inner's faces, so neither path can drift from the other.</summary>
public static class ModelObserving
{
    /// <summary>Picks the observing wrapper whose implemented faces MIRROR <paramref name="inner"/>'s — the entry point for a hand-built registry.</summary>
    public static ILLMClient Wrap(ILLMClient inner) => inner switch
    {
        IStructuredLLMClient and IStreamingLLMClient => new ModelObservingStreamingStructuredLLMClientDecorator(inner),
        IStructuredLLMClient => new ModelObservingStructuredLLMClientDecorator(inner),
        _ => new ModelObservingLLMClientDecorator(inner),
    };

    /// <summary>
    /// Register the observing wrapper over every <see cref="ILLMClient"/> in a container — the DI counterpart of
    /// <see cref="Wrap"/>, for a gate whose live brain resolves its client through DI (the whole-loop E2E reads its
    /// credential from a seeded DB row, so it can never hand-build a registry). Call it AFTER the production module so
    /// it lands OUTERMOST, over the recording decorators. The three-way conditional split mirrors the production
    /// recording family: a wrapper never claims a face its inner lacks.
    /// </summary>
    public static void RegisterDecorators(ContainerBuilder builder)
    {
        builder.RegisterDecorator<ModelObservingStreamingStructuredLLMClientDecorator, ILLMClient>(context => context.CurrentInstance is IStructuredLLMClient && context.CurrentInstance is IStreamingLLMClient);
        builder.RegisterDecorator<ModelObservingStructuredLLMClientDecorator, ILLMClient>(context => context.CurrentInstance is IStructuredLLMClient && context.CurrentInstance is not IStreamingLLMClient);
        builder.RegisterDecorator<ModelObservingLLMClientDecorator, ILLMClient>(context => context.CurrentInstance is not IStructuredLLMClient);
    }
}
