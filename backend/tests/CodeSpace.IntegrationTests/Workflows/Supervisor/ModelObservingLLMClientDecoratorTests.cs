using Autofac;
using Shouldly;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;

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

    /// <summary>A DISTINCT answering model per face. The sink keeps the LAST write, so driving all three faces with one name would let a face whose observation was gutted still pass on a sibling's write — each face is therefore asserted on its own name's fingerprint, in its own assessment.</summary>
    private const string AnsweredText = "gateway-answered-the-text-call";
    private const string AnsweredStructured = "gateway-answered-the-structured-call";
    private const string AnsweredStreamed = "gateway-answered-the-streamed-call";

    private static readonly System.Text.Json.JsonElement EmptySchema = System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone();

    [Fact]
    public async Task A_hand_built_registrys_wrapped_client_reports_the_answering_model_to_the_gates_sink()
    {
        // The trajectory gate's path: it constructs its own registry rather than resolving one, so the wrapper has to
        // be applied at construction or the gate observes nothing.
        var registry = new LLMClientRegistry(new[] { ModelObserving.Wrap(new FakeWireClient()) });

        await EveryFaceIsObservedAsync(registry.Resolve(FakeWireClient.ProviderTag));
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

        await EveryFaceIsObservedAsync(container.Resolve<ILLMClient>());
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

        var (answered, leftForAHuman) = await UnattendedAskResponder.AnswerAllAsync(
            answer => { answers.Add(answer); return Task.FromResult(parked-- > 0 ? ParkedAskDisposition.Answered : ParkedAskDisposition.NothingParked); },
            () => { drains++; return Task.CompletedTask; });

        answered.ShouldBe(3);
        leftForAHuman.ShouldBe(0);
        drains.ShouldBe(3, "each answered card's resume must be ridden to settlement before the next card is looked for");
        answers.Count.ShouldBe(4, "the loop stops on the first call that reports nothing parked");
        answers.ShouldAllBe(a => a.StartsWith(SupervisorApprovalRequest.ApproveReply, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_unattended_responder_is_bounded_so_a_brain_that_only_asks_terminates_as_a_miss()
    {
        // A brain that answers every answer with another question must not hang the lane — it must fall out and be
        // scored on whatever terminal the run actually reached.
        var (answered, _) = await UnattendedAskResponder.AnswerAllAsync(_ => Task.FromResult(ParkedAskDisposition.Answered), () => Task.CompletedTask);

        answered.ShouldBe(UnattendedAskResponder.MaxAnsweredAsks);
        UnattendedAskResponder.MaxAnsweredAsks.ShouldBeGreaterThan(SupervisorLane.DefaultMaxNoProgressDecisions,
            "a bound at or below the no-progress bound would starve a run that legitimately needs that many answers — the exact stall this responder exists to prevent");
    }

    [Fact]
    public void An_amend_cosign_card_is_left_parked_because_a_run_may_never_mark_its_own_homework()
    {
        // A REAL amend card, built by the production projector. It is an ask_human like any other, and
        // IsApprovedAmendCard approves on exactly the "approve" prefix the scripted answer carries — so a blanket
        // responder would co-sign the brain's own proposal to WAIVE the oracle its acceptance grade is measured
        // against, and the arm would then read a PASSED grade off the check the model talked its way out of.
        var amendCard = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s1",
            Waive = true,
            Reason = "the gate binary is missing",
        });

        UnattendedAskResponder.MustLeaveForAHuman(amendCard.PayloadJson)
            .ShouldBeTrue("an oracle amendment is the one card a script must never rule on — it stays parked for a real human");

        // Every OTHER card family stays answerable: refusing them all would put the headline arm straight back into the
        // unanswered-ask stall this responder exists to clear.
        UnattendedAskResponder.MustLeaveForAHuman(QuestionCard(SupervisorPlanConfirmation.ConfirmationMarker)).ShouldBeFalse("a plan confirmation is the operator's routine go-ahead");
        UnattendedAskResponder.MustLeaveForAHuman(QuestionCard(SupervisorGateEscalation.EscalationMarker)).ShouldBeFalse("a gate escalation is a ruling an operator makes");
        UnattendedAskResponder.MustLeaveForAHuman(QuestionCard("Which log format should I use?")).ShouldBeFalse("a plain content ask is answerable by anyone");
        UnattendedAskResponder.MustLeaveForAHuman(null).ShouldBeFalse("no card at all is not a refusal");

        static string QuestionCard(string question) =>
            System.Text.Json.JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = question }, AgentJson.Options);
    }

    [Fact]
    public async Task The_responder_stops_at_an_amend_card_and_reports_it_rather_than_answering_it()
    {
        // Two ordinary cards, then an amend card. The answer surface only ever targets the NEWEST ask, so nothing
        // behind the refused card is reachable — the loop must stop there and SAY so, not spin on it.
        var script = new Queue<ParkedAskDisposition>(new[] { ParkedAskDisposition.Answered, ParkedAskDisposition.Answered, ParkedAskDisposition.LeftForAHuman, ParkedAskDisposition.Answered });

        var (answered, leftForAHuman) = await UnattendedAskResponder.AnswerAllAsync(_ => Task.FromResult(script.Dequeue()), () => Task.CompletedTask);

        answered.ShouldBe(2);
        leftForAHuman.ShouldBe(1, "the refused card is COUNTED, so the verdict line says the attempt stopped at an oracle amendment rather than silently reading as a plain miss");
        script.Count.ShouldBe(1, "the loop stopped at the refused card — it never looked past it");
    }

    /// <summary>Run <paramref name="drive"/> inside a gate assessment and return the verdict stamp it produced — the same seam the gate's own reporting reads, so the assertion is on what a CI log would actually show. A FRESH assessment per call, because the sink keeps only the last write.</summary>
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

    /// <summary>Assert EACH face the wrapper carries feeds the sink — the decider reaches the structured one, the streaming callers the tee'd one, an llm.complete node the plain one. One assessment per face, each asserting its OWN answering model, so gutting any single face's observation reds this.</summary>
    private static async Task EveryFaceIsObservedAsync(ILLMClient client)
    {
        await ObservesAsync(AnsweredText, () => client.CompleteAsync(TextRequest(), CancellationToken.None));
        await ObservesAsync(AnsweredStructured, () => ((IStructuredLLMClient)client).CompleteStructuredAsync(new StructuredLLMCompletionRequest { Model = AskedFor, SystemPrompt = "s", UserPrompt = "p", JsonSchema = EmptySchema }, CancellationToken.None));
        await ObservesAsync(AnsweredStreamed, async () => { await foreach (var _ in ((IStreamingLLMClient)client).StreamAsync(TextRequest(), CancellationToken.None)) { } });
    }

    /// <summary>Drive one call inside a gate assessment and assert the verdict stamp names the model the PROVIDER answered with. Asserted through the masking-proof FINGERPRINT and the source tag rather than the raw name, so the assertion survives a stamp that prints the fingerprint alone (the model id is a repository secret; a stamp is free to stop printing it).</summary>
    private static async Task ObservesAsync(string answeringModel, Func<Task> call)
    {
        var stamp = await StampAfterAsync(call);

        stamp.ShouldContain($"fp={RealModelGate.Fingerprint(answeringModel)}", Case.Sensitive, $"the stamp must fingerprint the model the provider ANSWERED with ({answeringModel})");
        stamp.ShouldNotContain($"fp={RealModelGate.Fingerprint(AskedFor)}", Case.Sensitive, "the asked-for model id must never be what the verdict reports");
        stamp.ShouldNotContain("(configured)", Case.Sensitive, "a stamp tagged (configured) means nothing fed the sink — the gate cannot name the model behind its own verdict");
    }

    private static LLMCompletionRequest TextRequest() => new() { Model = AskedFor, SystemPrompt = "s", UserPrompt = "p" };

    /// <summary>A wire client that answers with a DIFFERENT model than the one requested — the gateway-multiplexing shape the stamp exists to catch.</summary>
    private sealed class FakeWireClient : ILLMClient, IStructuredLLMClient, IStreamingLLMClient
    {
        public const string ProviderTag = "FakeWire";

        public string Provider => ProviderTag;

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "t", Model = AnsweredText, Usage = LlmUsage.None });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion { Json = EmptySchema, Model = AnsweredStructured, Usage = LlmUsage.None });

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new LlmStreamEvent.Meta(Model: AnsweredStreamed);
            yield return new LlmStreamEvent.TextDelta("t");
            await Task.CompletedTask;
        }
    }

    private sealed class FakeStructuredOnlyClient : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "FakeStructuredOnly";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "t", Model = AnsweredText, Usage = LlmUsage.None });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion { Json = EmptySchema, Model = AnsweredStructured, Usage = LlmUsage.None });
    }

    private sealed class FakeTextOnlyClient : ILLMClient
    {
        public string Provider => "FakeTextOnly";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "t", Model = AnsweredText, Usage = LlmUsage.None });
    }
}
