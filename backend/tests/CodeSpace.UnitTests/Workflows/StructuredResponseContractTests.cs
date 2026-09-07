using System.Net;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Messages.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class StructuredResponseContractTests
{
    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_consumer_contract_violation_reasks_the_model_and_preserves_its_exact_corrected_payload(string provider)
    {
        var handler = new WireHandler(provider, ["{}", """{"argv":["/bin/sh",""," ","oracle.sh"]}"""]);
        var response = await Client(provider, handler).CompleteStructuredAsync(Request(provider), CancellationToken.None);
        handler.Bodies.Count.ShouldBe(2);
        handler.Bodies[1].ShouldContain("consumer requires non-empty argv");
        response.Json.GetProperty("argv").EnumerateArray().Select(x => x.GetString()).ToArray().ShouldBe(new[] { "/bin/sh", "", " ", "oracle.sh" });
        response.Usage.InputTokens.ShouldBe(24);
        response.Usage.OutputTokens.ShouldBe(14);
        response.Usage.IsPartial.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task Repeated_consumer_contract_failure_is_typed_and_bounded(string provider)
    {
        var handler = new WireHandler(provider, ["{}", "{}"]);
        var error = await Should.ThrowAsync<LlmApiException>(() => Client(provider, handler).CompleteStructuredAsync(Request(provider), CancellationToken.None));
        error.Category.ShouldBe(LlmErrorCategory.Malformed);
        error.Message.ShouldContain("consumer requires non-empty argv");
        handler.Bodies.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_valid_response_uses_one_call_and_the_validator_is_never_serialized(string provider)
    {
        var request = Request(provider) with { ResponseAdvisor = _ => [] };
        JsonSerializer.Serialize(request).ShouldNotContain("ResponseValidator");
        JsonSerializer.Serialize(request).ShouldNotContain("ResponseAdvisor", customMessage: "the advisor is a server-side callback like the validator — a delegate on the wire is a leak, not a field");
        var handler = new WireHandler(provider, ["""{"argv":["arbitrary-executable",""]}"""]);
        (await Client(provider, handler).CompleteStructuredAsync(request, CancellationToken.None)).Json.GetProperty("argv")[1].GetString().ShouldBe("");
        handler.Bodies.Count.ShouldBe(1);
        handler.Bodies[0].ShouldNotContain("ResponseValidator");
        handler.Bodies[0].ShouldNotContain("ResponseAdvisor");
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task Cancellation_from_the_consumer_does_not_trigger_another_model_call(string provider)
    {
        var handler = new WireHandler(provider, ["{}"]);
        var request = Request(provider) with { ResponseValidator = _ => throw new OperationCanceledException() };
        await Should.ThrowAsync<OperationCanceledException>(() => Client(provider, handler).CompleteStructuredAsync(request, CancellationToken.None));
        handler.Bodies.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("Anthropic", "TestsPass", "argv")]
    [InlineData("OpenAI", "TestsPass", "argv")]
    [InlineData("Anthropic", "ArtifactPresent", "artifactPaths")]
    [InlineData("OpenAI", "ArtifactPresent", "artifactPaths")]
    public async Task The_actual_planner_request_repairs_missing_oracle_payload_through_the_existing_provider_path(string provider, string kind, string payloadName)
    {
        var bad = "{\"goal\":\"produce the requested result\",\"subtasks\":[{\"id\":\"s1\",\"title\":\"result\",\"instruction\":\"do the work\",\"acceptance\":{\"formatVersion\":2,\"kind\":\"" + kind + "\"}}],\"successCriteria\":[],\"risks\":[],\"recommendedWorkflowKind\":\"coding\"}";
        var payload = kind == "TestsPass" ? "[\"arbitrary-command\",\"\",\" \"]" : "[\"chosen-result.bin\"]";
        var good = bad.Replace("\"kind\":\"" + kind + "\"", "\"kind\":\"" + kind + "\",\"" + payloadName + "\":" + payload);
        var request = LlmWorkflowPlanner.BuildRequest(new WorkflowPlanRequest { TaskText = "produce the result", TeamId = Guid.NewGuid() }, new ModelPoolPick { ModelId = "wire-test-model", Credential = new ResolvedModelCredential { Provider = provider, ApiKey = "fixture-key" } }, "", []);
        var handler = new WireHandler(provider, [bad, good]);
        var response = await Client(provider, handler).CompleteStructuredAsync(request, CancellationToken.None);
        handler.Bodies.Count.ShouldBe(2);
        handler.Bodies[1].ShouldContain(payloadName);
        var plan = LlmWorkflowPlanner.Deserialize(response.Json);
        plan.DroppedAcceptances.ShouldBeNull("the re-ask authored the payload — a repaired reply records NO defect (the drop is not a receipt for having asked)");
        var actual = plan.Subtasks.Single().Acceptance.ShouldNotBeNull().Command;
        actual.ShouldBe(JsonDocument.Parse(payload).RootElement.EnumerateArray().Select(x => x.GetString()).ToArray());
        response.Usage.InputTokens.ShouldBe(24);
        response.Usage.OutputTokens.ShouldBe(14);
    }

    [Theory]
    [InlineData("Anthropic", "TestsPass", "argv")]
    [InlineData("OpenAI", "TestsPass", "argv")]
    [InlineData("Anthropic", "ArtifactPresent", "artifactPaths")]
    [InlineData("OpenAI", "ArtifactPresent", "artifactPaths")]
    public async Task A_planner_reply_that_never_authors_the_payload_is_returned_after_its_one_reask_instead_of_killing_the_plan(string provider, string kind, string payloadName)
    {
        // Live run 34084564329: the model skipped ONE acceptance payload, the re-ask did not fix it, and the reply
        // became a Malformed fault — so the planner NODE failed and a plan-map launch died at planning. A
        // model-quality miss must cost that subtask its oracle, never the run its plan.
        var handler = new WireHandler(provider, [PlannerReply(kind), PlannerReply(kind)]);

        var response = await Client(provider, handler).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None);

        handler.Bodies.Count.ShouldBe(2, "the bounded re-ask still fires — the model gets its one chance to author the payload");
        // The ADVICE's own wording, not merely the id: asserting on "s1" alone is satisfied by the echoed previous
        // reply, which quotes every id already. (The quoted id itself rides as 's1' — the request body's
        // JSON encoder escapes apostrophes — so the id-naming half is pinned at the advisor in PlannerAcceptanceMappingTests.)
        handler.Bodies[1].ShouldContain($"chose acceptance kind {kind} but authored no payload for it");

        var plan = LlmWorkflowPlanner.Deserialize(response.Json);
        plan.Subtasks.Single().Acceptance.ShouldBeNull("no oracle, never a guessed one");
        var drop = plan.DroppedAcceptances.ShouldHaveSingleItem();
        drop.SubtaskId.ShouldBe("s1");
        drop.Reason.ShouldContain(payloadName);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_reask_that_makes_an_advisory_reply_fatal_returns_the_first_reply_instead_of_the_fault(string provider)
    {
        // The trade this forbids: the first reply is schema-VALID and only advisory, so it is already an answer the
        // planner degrades. Advice that names a payload to author can steer the model into authoring it on the WRONG
        // oracle (argv on a file oracle), which is fatal BY DESIGN — so "re-ask, then trust the second reply" turned a
        // degradable plan into a dead run. The upgrade attempt may only replace the answer, never destroy it.
        var worse = PlannerReply("ArtifactPresent").Replace("\"kind\":\"ArtifactPresent\"", "\"kind\":\"ArtifactPresent\",\"argv\":[\"test\",\"-f\",\"report.md\"]");
        var handler = new WireHandler(provider, [PlannerReply("ArtifactPresent"), worse]);

        var response = await Client(provider, handler).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None);

        handler.Bodies.Count.ShouldBe(2, "the re-ask is still bounded to exactly one");
        response.Json.GetProperty("subtasks")[0].GetProperty("acceptance").TryGetProperty("argv", out _)
            .ShouldBeFalse("the returned reply is the FIRST one — the reinterpretable second reply is discarded, never handed on");

        var plan = LlmWorkflowPlanner.Deserialize(response.Json);
        plan.Subtasks.Single().Acceptance.ShouldBeNull();
        plan.DroppedAcceptances.ShouldHaveSingleItem().SubtaskId.ShouldBe("s1", "the drop is recorded from the reply that was actually kept");

        response.Usage.InputTokens.ShouldBe(24, "both physical calls were billed — keeping the first ANSWER never under-reports the cost of asking twice");
        response.Usage.OutputTokens.ShouldBe(14);
        response.Usage.IsPartial.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_reask_that_returns_no_json_at_all_returns_the_first_advisory_reply(string provider)
    {
        // The same rule for the other way a re-ask can fail: the second call produces no parseable JSON, so
        // CompleteStructuredOnceAsync itself raises Malformed. An advisory-only first reply outlives that too.
        var handler = new WireHandler(provider, [PlannerReply("TestsPass"), "I am sorry, I cannot author that plan."]);

        var response = await Client(provider, handler).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None);

        handler.Bodies.Count.ShouldBe(3, "the re-ask degrades through the provider's own prompt-only floor before it gives up");
        LlmWorkflowPlanner.Deserialize(response.Json).DroppedAcceptances.ShouldHaveSingleItem().SubtaskId.ShouldBe("s1");

        response.Usage.InputTokens.ShouldBe(12, "the failed attempt's usage died with its exception — only the kept reply's own counts are known");
        response.Usage.IsPartial.ShouldBeTrue("a subtotal must say so rather than price the whole call");
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_fatal_planner_contract_violation_is_still_a_bounded_typed_fault(string provider)
    {
        // The degrade is scoped to the ONE absent-payload shape. A payload the server would have to reinterpret
        // (argv on a file oracle) stays fatal — the alternative is a silently mis-typed oracle.
        var bad = "{\"goal\":\"g\",\"subtasks\":[{\"id\":\"s1\",\"title\":\"t\",\"instruction\":\"i\",\"acceptance\":{\"formatVersion\":2,\"kind\":\"ArtifactPresent\",\"argv\":[\"test\",\"-f\",\"report.md\"]}}]}";
        var handler = new WireHandler(provider, [bad, bad]);

        var error = await Should.ThrowAsync<LlmApiException>(() => Client(provider, handler).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None));

        error.Category.ShouldBe(LlmErrorCategory.Malformed);
        handler.Bodies.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task The_reask_preamble_calls_a_schema_miss_invalid_and_an_advisory_conformant(string provider)
    {
        // One preamble cannot serve both severities. Telling a model its schema-VALID reply "did NOT conform to the
        // required JSON Schema … (invalid)" is a false correction on the exact reply the consumer was going to accept,
        // and it invites the model to re-author the parts that were already right.
        var fatal = new WireHandler(provider, ["{}", "{}"]);
        await Should.ThrowAsync<LlmApiException>(() => Client(provider, fatal).CompleteStructuredAsync(Request(provider), CancellationToken.None));

        fatal.Bodies[1].ShouldContain("did NOT conform to the required JSON Schema");
        fatal.Bodies[1].ShouldContain("previous (invalid) response");

        var advisory = new WireHandler(provider, [PlannerReply("TestsPass"), PlannerReply("TestsPass")]);
        await Client(provider, advisory).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None);

        advisory.Bodies[1].ShouldContain("conformed to the required JSON Schema");
        advisory.Bodies[1].ShouldContain("left a required payload unauthored");
        advisory.Bodies[1].ShouldNotContain("did NOT conform", customMessage: "the reply DID conform — the schema does not require the payload this advice asks for");
        advisory.Bodies[1].ShouldNotContain("previous (invalid) response", customMessage: "an advisory reply is not invalid; calling it that is a lie the model then acts on");
    }

    /// <summary>The live regression shape: one subtask that names an oracle kind and authors NO payload for it — schema-valid (the schema requires only formatVersion + kind), so the defect is the consumer contract's alone.</summary>
    private static string PlannerReply(string kind) =>
        "{\"goal\":\"produce the requested result\",\"subtasks\":[{\"id\":\"s1\",\"title\":\"result\",\"instruction\":\"do the work\",\"acceptance\":{\"formatVersion\":2,\"kind\":\"" + kind + "\"}}],\"successCriteria\":[],\"risks\":[],\"recommendedWorkflowKind\":\"coding\"}";

    /// <summary>The REAL planner request (schema + validator + advisor as production builds them) — never a stand-in, so these arms pin the wire the live run used.</summary>
    private static StructuredLLMCompletionRequest PlannerRequest(string provider) =>
        LlmWorkflowPlanner.BuildRequest(new WorkflowPlanRequest { TaskText = "produce the result", TeamId = Guid.NewGuid() }, new ModelPoolPick { ModelId = "wire-test-model", Credential = new ResolvedModelCredential { Provider = provider, ApiKey = "fixture-key" } }, "", []);

    private static StructuredLLMCompletionRequest Request(string provider) => new()
    {
        Model = "wire-test-model", SystemPrompt = "Return data", UserPrompt = "Create the requested result", JsonSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
        Credential = new ResolvedModelCredential { Provider = provider, ApiKey = "fixture-key" },
        ResponseValidator = json => json.TryGetProperty("argv", out var argv) && argv.ValueKind == JsonValueKind.Array && argv.GetArrayLength() > 0 ? [] : ["consumer requires non-empty argv"],
    };

    private static IStructuredLLMClient Client(string provider, WireHandler handler) => provider == "Anthropic" ? new AnthropicClient(new Factory(handler)) : new OpenAiClient(new Factory(handler));
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class WireHandler(string provider, string[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var content = responses[Math.Min(Bodies.Count - 1, responses.Length - 1)];

            // A queued entry that is not a JSON object is PROSE — what a model returns when it answers in text
            // instead of the forced tool/function call. Both clients then find no JSON, degrade to their prompt-only
            // floor, and raise Malformed: the arm where a re-ask produces nothing usable at all.
            var prose = !content.TrimStart().StartsWith('{');

            var response = provider == "Anthropic"
                ? "{\"model\":\"wire-test-model\",\"content\":[" + (prose
                    ? "{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(content) + "}"
                    : "{\"type\":\"tool_use\",\"id\":\"call_fixture\",\"name\":\"respond\",\"input\":" + content + "}") + "],\"usage\":{\"input_tokens\":12,\"output_tokens\":7}}"
                : "{\"model\":\"wire-test-model\",\"choices\":[{\"message\":{\"role\":\"assistant\"," + (prose
                    ? "\"content\":" + JsonSerializer.Serialize(content)
                    : "\"tool_calls\":[{\"id\":\"call_fixture\",\"type\":\"function\",\"function\":{\"name\":\"respond\",\"arguments\":" + JsonSerializer.Serialize(content) + "}}]") + "},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":7}}";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
