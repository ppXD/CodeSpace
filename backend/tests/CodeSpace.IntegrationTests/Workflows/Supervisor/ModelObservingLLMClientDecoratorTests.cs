using Autofac;
using Shouldly;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Pins the two things a real-model gate's model-identity stamp actually depends on:
///
/// <list type="number">
/// <item>the observing wrapper reaches <see cref="RealModelGate.ObserveModel"/> on BOTH construction paths — the
/// hand-built registry (<see cref="ModelObserving.Wrap"/>, the trajectory + decision gates) and the DI-resolved client
/// (<see cref="ModelObserving.RegisterDecorators"/>, the whole-loop E2E whose brain reads its credential from a DB row).
/// Both were bypassed before this: all ten trajectory stamps and sixteen whole-loop stamps read the asked-for secret id
/// tagged <c>(configured)</c>, so a gateway quietly answering with a different model looked exactly like a capability
/// regression (real-model run 33723910434);</item>
/// <item>the unattended answer loop answers every parked card with an APPROVING answer and stays bounded — the surface
/// without which the whole-loop headline gate can only be met by a model that never asks a question.</item>
/// </list>
///
/// Driven entirely through the pure seams (a fake client, a bare container, two delegates), so nothing here needs a
/// live model, a database, or process env.
/// </summary>
public sealed class ModelObservingLLMClientDecoratorTests
{
    private const string AskedFor = "the-configured-secret-id";
    private const string Answered = "gateway-actually-answered-this";

    private static readonly System.Text.Json.JsonElement EmptySchema = System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone();

    [Fact]
    public async Task A_hand_built_registrys_wrapped_client_reports_the_answering_model_to_the_gates_sink()
    {
        // The trajectory gate's path: it constructs its own registry rather than resolving one, so the wrapper has to
        // be applied at construction or the gate observes nothing.
        var registry = new LLMClientRegistry(new[] { ModelObserving.Wrap(new FakeWireClient()) });

        var stamp = await StampAfterAsync(() => CallEveryFaceAsync(registry.Resolve(FakeWireClient.ProviderTag)));

        stamp.ShouldContain($"model={Answered}");
        stamp.ShouldNotContain(AskedFor, Case.Sensitive, "the PROVIDER-reported model must win over the one that was asked for");
        stamp.ShouldNotContain("(configured)", Case.Sensitive, "a stamp reading (configured) means nothing fed the sink — the gate cannot name the model behind its own verdict");
    }

    [Fact]
    public async Task A_DI_resolved_client_reports_the_answering_model_to_the_gates_sink()
    {
        // The whole-loop E2E's path: its live brain resolves the client through the container (its credential lives in a
        // seeded DB row), so the wrapper has to be a decorator registration or the gate observes nothing.
        var builder = new ContainerBuilder();
        builder.RegisterType<FakeWireClient>().As<ILLMClient>().As<IStructuredLLMClient>().As<IStreamingLLMClient>().SingleInstance();
        ModelObserving.RegisterDecorators(builder);

        using var container = builder.Build();

        var stamp = await StampAfterAsync(() => CallEveryFaceAsync(container.Resolve<ILLMClient>()));

        stamp.ShouldContain($"model={Answered}");
        stamp.ShouldNotContain("(configured)", Case.Sensitive);
    }

    [Fact]
    public void The_DI_wrapper_mirrors_the_inner_clients_faces_so_a_consumer_feature_detect_is_never_fooled()
    {
        // The decider casts to IStructuredLLMClient and the streaming callers to IStreamingLLMClient; a wrapper that
        // claimed a face its inner lacks would land those casts on a client that cannot serve them (and the merge
        // synthesis's `is not IStructuredLLMClient` pick would silently change meaning).
        Wrapped(new FakeWireClient()).ShouldBeAssignableTo<IStreamingLLMClient>();
        Wrapped(new FakeStructuredOnlyClient()).ShouldBeAssignableTo<IStructuredLLMClient>();
        Wrapped(new FakeStructuredOnlyClient()).ShouldNotBeAssignableTo<IStreamingLLMClient>();
        Wrapped(new FakeTextOnlyClient()).ShouldNotBeAssignableTo<IStructuredLLMClient>();

        static ILLMClient Wrapped(ILLMClient inner)
        {
            var builder = new ContainerBuilder();
            builder.RegisterInstance(inner).As<ILLMClient>();
            ModelObserving.RegisterDecorators(builder);

            return builder.Build().Resolve<ILLMClient>();
        }
    }

    [Fact]
    public async Task The_unattended_responder_answers_every_parked_card_with_an_approving_answer()
    {
        // N scripted cards, then nothing parked. Every answer must lead with the production approval word: a card the
        // human "answers" with anything else is a REJECTION to the plan-confirmation / gate-escalation / amend readers,
        // which would drive the run somewhere else entirely rather than letting it proceed.
        var parked = 3;
        var answers = new List<string>();
        var drains = 0;

        var answered = await UnattendedAskResponder.AnswerAllAsync(
            answer => { answers.Add(answer); return Task.FromResult(parked-- > 0); },
            () => { drains++; return Task.CompletedTask; });

        answered.ShouldBe(3);
        drains.ShouldBe(3, "each answered card's resume must be ridden to settlement before the next card is looked for");
        answers.Count.ShouldBe(4, "the loop stops on the first call that reports nothing parked");
        answers.ShouldAllBe(a => a.StartsWith(SupervisorApprovalRequest.ApproveReply, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_unattended_responder_is_bounded_so_a_brain_that_only_asks_terminates_as_a_miss()
    {
        // A brain that answers every answer with another question must not hang the lane — it must fall out and be
        // scored on whatever terminal the run actually reached.
        var answered = await UnattendedAskResponder.AnswerAllAsync(_ => Task.FromResult(true), () => Task.CompletedTask);

        answered.ShouldBe(UnattendedAskResponder.MaxAnsweredAsks);
        UnattendedAskResponder.MaxAnsweredAsks.ShouldBeGreaterThan(SupervisorLane.DefaultMaxNoProgressDecisions,
            "a bound at or below the no-progress bound would starve a run that legitimately needs that many answers — the exact stall this responder exists to prevent");
    }

    /// <summary>Run <paramref name="drive"/> inside a gate assessment and return the verdict stamp it produced — the same seam the gate's own reporting reads, so the assertion is on what a CI log would actually show.</summary>
    private static async Task<string> StampAfterAsync(Func<Task> drive)
    {
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-observing-{Guid.NewGuid():N}.md");
        try
        {
            await RealModelGate.AssessLiveAsync("OpenAI", async () =>
            {
                await drive();
                return (true, "drove");
            }, gating: false, stepSummaryPath: path);

            return await File.ReadAllTextAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Exercise every face the wrapper carries — the decider reaches the structured one, the streaming callers the tee'd one, an llm.complete node the plain one. Each must feed the sink; a face that passes through blind is a stamp the gate cannot trust.</summary>
    private static async Task CallEveryFaceAsync(ILLMClient client)
    {
        await client.CompleteAsync(TextRequest(), CancellationToken.None);
        await ((IStructuredLLMClient)client).CompleteStructuredAsync(new StructuredLLMCompletionRequest { Model = AskedFor, SystemPrompt = "s", UserPrompt = "p", JsonSchema = EmptySchema }, CancellationToken.None);

        await foreach (var _ in ((IStreamingLLMClient)client).StreamAsync(TextRequest(), CancellationToken.None)) { }
    }

    private static LLMCompletionRequest TextRequest() => new() { Model = AskedFor, SystemPrompt = "s", UserPrompt = "p" };

    /// <summary>A wire client that answers with a DIFFERENT model than the one requested — the gateway-multiplexing shape the stamp exists to catch.</summary>
    private sealed class FakeWireClient : ILLMClient, IStructuredLLMClient, IStreamingLLMClient
    {
        public const string ProviderTag = "FakeWire";

        public string Provider => ProviderTag;

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "t", Model = Answered, Usage = LlmUsage.None });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion { Json = EmptySchema, Model = Answered, Usage = LlmUsage.None });

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new LlmStreamEvent.Meta(Model: Answered);
            yield return new LlmStreamEvent.TextDelta("t");
            await Task.CompletedTask;
        }
    }

    private sealed class FakeStructuredOnlyClient : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "FakeStructuredOnly";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "t", Model = Answered, Usage = LlmUsage.None });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion { Json = EmptySchema, Model = Answered, Usage = LlmUsage.None });
    }

    private sealed class FakeTextOnlyClient : ILLMClient
    {
        public string Provider => "FakeTextOnly";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "t", Model = Answered, Usage = LlmUsage.None });
    }
}
