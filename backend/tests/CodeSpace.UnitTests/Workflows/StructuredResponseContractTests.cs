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
    // CitationsResolve, not ArtifactPresent: both share the plain artifactPaths shape, but P2.6 additionally drops a
    // bare ArtifactPresent as self-certifying (PlannerAcceptanceMappingTests covers that gate) — an unrelated defect
    // this arm is not about, which would otherwise mask the repair verdict this test actually asserts.
    [InlineData("Anthropic", "CitationsResolve", "artifactPaths")]
    [InlineData("OpenAI", "CitationsResolve", "artifactPaths")]
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
    [InlineData("Anthropic", "TestsPass", "argv", "")]
    [InlineData("OpenAI", "TestsPass", "argv", "")]
    [InlineData("Anthropic", "ArtifactPresent", "artifactPaths", "")]
    [InlineData("OpenAI", "ArtifactPresent", "artifactPaths", "")]
    // An EMPTY authored payload is the same defect reported by a different schema keyword — `minItems` on the payload
    // rather than "no oneOf branch matches" — so both arrive as violations of the acceptance's own schema, not of the
    // consumer contract alone. A degrade that only survived one of the two would still kill live plans.
    [InlineData("Anthropic", "TestsPass", "argv", ",\"argv\":[]")]
    [InlineData("OpenAI", "ArtifactPresent", "artifactPaths", ",\"artifactPaths\":[]")]
    // The arm no keyword could classify: an unmatched `oneOf` spills its CLOSEST branch's violations, and for a
    // payload-less LlmJudge that branch is another kind's — so the schema faults a `kind` the model got right. The
    // text reads exactly like a genuinely bad enum value; only the acceptance contract's own verdict tells them apart.
    [InlineData("Anthropic", "LlmJudge", "artifactPaths", "")]
    [InlineData("OpenAI", "LlmJudge", "artifactPaths", "")]
    public async Task A_planner_reply_that_never_authors_the_payload_is_returned_after_its_one_reask_instead_of_killing_the_plan(string provider, string kind, string payloadName, string payload)
    {
        // Live run 34084564329: the model skipped ONE acceptance payload, the re-ask did not fix it, and the reply
        // became a Malformed fault — so the planner NODE failed and a plan-map launch died at planning. A
        // model-quality miss must cost that subtask its oracle, never the run its plan.
        var handler = new WireHandler(provider, [PlannerReply(kind, payload), PlannerReply(kind, payload)]);

        var response = await Client(provider, handler).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None);

        handler.Bodies.Count.ShouldBe(2, "the bounded re-ask still fires — the model gets its one chance to author the payload");
        // The ADVICE's own wording, not merely the id: asserting on "s1" alone is satisfied by the echoed previous
        // reply, which quotes every id already. (The quoted id itself rides as 's1' — the request body's
        // JSON encoder escapes apostrophes — so the id-naming half is pinned at the advisor in PlannerAcceptanceMappingTests.)
        handler.Bodies[1].ShouldContain("authored an acceptance this contract cannot bind");
        handler.Bodies[1].ShouldContain($"requires a non-empty {payloadName} array");

        var plan = LlmWorkflowPlanner.Deserialize(response.Json);
        plan.Subtasks.Single().Acceptance.ShouldBeNull("no oracle, never a guessed one");
        var drop = plan.DroppedAcceptances.ShouldHaveSingleItem();
        drop.SubtaskId.ShouldBe("s1");
        drop.Reason.ShouldContain(payloadName);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_reask_that_answers_with_a_different_unbindable_shape_returns_the_first_reply(string provider)
    {
        // The trade this forbids: the first reply is already an answer the planner degrades, so the re-ask is an
        // UPGRADE attempt. Advice that names a payload to author can steer the model into authoring it on the WRONG
        // oracle (argv on a file oracle) — a second reply whose acceptance still does not bind, only for a new
        // reason. Accepting it would swap one drop for another and discard a reply the consumer had already accepted.
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
    [InlineData("Anthropic", "{\"formatVersion\":2,\"kind\":\"ArtifactPresent\",\"argv\":[\"test\",\"-f\",\"report.md\"]}", "never both or the other payload")]
    [InlineData("OpenAI", "{\"formatVersion\":2,\"kind\":\"ArtifactPresent\",\"argv\":[\"test\",\"-f\",\"report.md\"]}", "never both or the other payload")]
    [InlineData("Anthropic", "{\"formatVersion\":2,\"kind\":\"NoSuchOracle\",\"argv\":[\"true\"]}", "'NoSuchOracle'")]
    [InlineData("OpenAI", "{\"kind\":\"ArtifactPresent\",\"command\":[\"report.md\"]}", "command")]
    public async Task An_acceptance_the_contract_refuses_to_bind_is_dropped_with_its_reason_rather_than_faulting(string provider, string acceptance, string reason)
    {
        // These three shapes were the last fatal ones: a payload the server would have to REINTERPRET (argv on a file
        // oracle), an oracle no grader exists for, and the v1 `command` key. None is reinterpreted now either — the
        // acceptance is dropped and the reason says which — but none of them costs the plan, because a live model
        // authors these on subtasks whose WORK is perfectly good (lane run 34093741284).
        var reply = PlannerReplyWithAcceptance(acceptance);
        var handler = new WireHandler(provider, [reply, reply]);

        var response = await Client(provider, handler).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None);

        handler.Bodies.Count.ShouldBe(2, "the re-ask stays bounded to one");
        handler.Bodies[1].ShouldNotContain("did NOT conform", customMessage: "a reply the consumer is about to accept must not be told it was invalid");

        var plan = LlmWorkflowPlanner.Deserialize(response.Json);
        plan.Subtasks.Single().Acceptance.ShouldBeNull("nothing is reinterpreted — dropping is the only degrade");
        plan.DroppedAcceptances.ShouldHaveSingleItem().Reason.ShouldContain(reason);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task An_acceptance_authored_as_null_survives_its_reask_instead_of_faulting_the_plan(string provider)
    {
        // `"acceptance": null` is what a model writes when it has no oracle to offer, and it used to read as "no
        // oracle authored" — so no advisory claimed the position and the schema's `expected type 'object' but got
        // null` there was fatal. Two identical replies therefore ended the call as Malformed and the planner node
        // parked the run over a subtask whose WORK was perfectly good.
        var reply = PlannerReplyWithAcceptance("null");
        var handler = new WireHandler(provider, [reply, reply]);

        var response = await Client(provider, handler).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None);

        handler.Bodies.Count.ShouldBe(2, "the re-ask stays bounded to one");
        handler.Bodies[1].ShouldContain("must be a JSON object");
        handler.Bodies[1].ShouldNotContain("did NOT conform", customMessage: "a reply the consumer is about to accept must not be told it was invalid");

        var plan = LlmWorkflowPlanner.Deserialize(response.Json);
        plan.Subtasks.Single().Acceptance.ShouldBeNull("nothing is unwrapped out of a non-object acceptance");
        plan.DroppedAcceptances.ShouldHaveSingleItem().Reason.ShouldContain("authored null");
    }

    [Theory]
    [InlineData("Anthropic", HttpStatusCode.TooManyRequests)]
    [InlineData("OpenAI", HttpStatusCode.TooManyRequests)]
    [InlineData("Anthropic", HttpStatusCode.ServiceUnavailable)]
    [InlineData("OpenAI", HttpStatusCode.ServiceUnavailable)]
    public async Task A_reask_that_fails_on_the_transport_returns_the_first_advisory_reply(string provider, HttpStatusCode status)
    {
        // The upgrade attempt can fail for a reason that has nothing to do with what it authored — a 429, a 5xx, a
        // dropped connection. Only `Malformed` was caught, so any of those DESTROYED a first reply the consumer had
        // already accepted, and the planner node parked the run to earn a fresh attempt at a plan we were holding.
        // We already have a usable answer: no transport failure on the upgrade attempt is worth losing it.
        var handler = new WireHandler(provider, [PlannerReply("TestsPass"), "{\"error\":{\"message\":\"synthetic re-ask failure\"}}"]) { Statuses = [HttpStatusCode.OK, status] };

        var response = await Client(provider, handler).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None);

        handler.Bodies.Count.ShouldBe(2, "a 429 / 5xx propagates out of the provider's own attempt rather than degrading to its prompt-only floor");
        LlmWorkflowPlanner.Deserialize(response.Json).DroppedAcceptances.ShouldHaveSingleItem().SubtaskId.ShouldBe("s1", "the drop is recorded from the reply that was actually kept");

        response.Usage.InputTokens.ShouldBe(12, "the failed attempt's usage died with its exception — only the kept reply's own counts are known");
        response.Usage.IsPartial.ShouldBeTrue("a subtotal must say so rather than price the whole call");
    }

    [Theory]
    [InlineData("Anthropic", "a schema-fatal first reply", 2)]
    [InlineData("OpenAI", "a schema-fatal first reply", 2)]
    [InlineData("Anthropic", "the floor after a billed forced attempt", 2)]
    [InlineData("OpenAI", "the floor after a billed forced attempt", 2)]
    [InlineData("Anthropic", "the re-ask after an unparseable reply", 3)]
    [InlineData("OpenAI", "the re-ask after an unparseable reply", 3)]
    public async Task A_failure_that_follows_a_BILLED_attempt_holds_the_one_reservation_covering_both(string provider, string shape, int physicalRequests)
    {
        // Every attempt of a progressive-then-re-asked structured call — forced tool/function use, the prompt-only
        // floor, the bounded re-ask — is a SEPARATE physical request billed separately, and all of them ride ONE
        // budget reservation. So a 429 on a LATER attempt proves nothing about an earlier one that already bought
        // tokens: unmarked, LlmBudgetGuard read that 429 as proven-unbilled and RELEASED headroom the run had really
        // spent. The three shapes are the three places a second request is made after a first one was billed.
        const string prose = "I will answer in prose instead of calling the tool.";
        const string gatewayError = """{"error":{"message":"slow down"}}""";

        var handler = shape switch
        {
            "a schema-fatal first reply" => new WireHandler(provider, ["{}", gatewayError]) { Statuses = [HttpStatusCode.OK, HttpStatusCode.TooManyRequests] },
            "the floor after a billed forced attempt" => new WireHandler(provider, [prose, gatewayError]) { Statuses = [HttpStatusCode.OK, HttpStatusCode.TooManyRequests] },
            _ => new WireHandler(provider, [prose, prose, gatewayError]) { Statuses = [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests] },
        };

        var ledger = new CountingBudgetLedger();
        var scope = new LlmCallScope(Guid.NewGuid(), Guid.NewGuid(), "planner", "planner#1", "llm.complete", null!, null!, ledger, CapUsd: 5m);
        var client = Client(provider, handler);

        var thrown = await Should.ThrowAsync<LlmApiException>(() => LlmBudgetGuard.GuardedAsync(scope, "claude-opus-4-8", "s", "u", 100,
            cancellationToken => client.CompleteStructuredAsync(Request(provider), cancellationToken), _ => (decimal?)null, CancellationToken.None));

        handler.Bodies.Count.ShouldBe(physicalRequests, customMessage: "the fixture has to actually have made the LATER request, or it measures nothing");

        thrown.StatusCode.ShouldBe(429, customMessage: "the status / category / retryability ride through the marking unchanged — every retry and degrade branch must behave exactly as it did");
        thrown.Category.ShouldBe(LlmErrorCategory.RateLimited);
        thrown.PriorBilledAttempt.ShouldBeTrue($"{shape} was a 2xx the provider billed under this reservation");
        LlmBudgetGuard.ObservedNoSpend(thrown).ShouldBeFalse("which is exactly what the guard must read off it");

        ledger.Releases.ShouldBe(0, "releasing here hands back headroom that really was spent — the defect this marking closes");
        ledger.Settles.ShouldBe(1);
        ledger.LastSettleActual.ShouldBeNull("the spend is unknowable (the billed reply died with the exception), so the reserve is HELD rather than priced");
    }

    /// <summary>Counts what the guard did with the reservation — the whole assertion of the settlement arms above.</summary>
    private sealed class CountingBudgetLedger : CodeSpace.Core.Services.Workflows.Budget.IBudgetLedger
    {
        public int Settles { get; private set; }
        public int Releases { get; private set; }
        public decimal? LastSettleActual { get; private set; }

        public Task<CodeSpace.Core.Services.Workflows.Budget.BudgetAdmission> ReserveAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal estimateUsd, decimal? capUsd, string priceVersion, Guid? parentReservationId, DateTimeOffset? expiresAt, CancellationToken cancellationToken) =>
            Task.FromResult(new CodeSpace.Core.Services.Workflows.Budget.BudgetAdmission(true, Guid.NewGuid(), 0m, capUsd, null));

        public Task SettleAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken)
        {
            Settles++;
            LastSettleActual = actualUsd;
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, CancellationToken cancellationToken)
        {
            Releases++;
            return Task.CompletedTask;
        }

        public Task<int> ExpireOverdueAsync(int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<decimal> CommittedUsdAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult(0m);
        public Task<decimal> CommittedTeamUsdAsync(Guid teamId, DateTimeOffset since, CancellationToken cancellationToken) => Task.FromResult(0m);
        public Task<int> ReconcileDanglingAsync(string kindPrefix, int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task The_live_four_subtask_reply_survives_its_reask_with_all_four_oracles_dropped(string provider)
    {
        // Lane run 34093741284 end to end over the real wire: four acceptances, none with `formatVersion`, three also
        // missing their payload. The whole plan came back as `planner.Status == Failure`. It must now come back as a
        // plan — with four subtasks, no oracles, and four named drops — after exactly one re-ask.
        var handler = new WireHandler(provider, [LiveFourSubtaskReply, LiveFourSubtaskReply]);

        var response = await Client(provider, handler).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None);

        handler.Bodies.Count.ShouldBe(2);
        handler.Bodies[1].ShouldContain("left part of itself unusable", customMessage: "four degradable defects are still four advisories, never a fault");

        // One re-ask for four defective acceptances: advice that names some of them buys a reply with the rest still
        // wrong. (An apostrophe rides the request body JSON-escaped, so undo that one escape rather than asserting on
        // the encoder's spelling of it.)
        var advice = handler.Bodies[1].Replace("\\u0027", "'");
        foreach (var id in new[] { "s1", "s2", "s3", "s4" })
            advice.ShouldContain($"Subtask '{id}' authored an acceptance this contract cannot bind", customMessage: "every degraded subtask has to be named, not just the first");
        advice.ShouldContain("requires formatVersion 2", customMessage: "the defect all four share");
        advice.ShouldContain("non-empty argv array");
        advice.ShouldContain("non-empty artifactPaths array", customMessage: "both payload classes are wrong in this reply; naming one of them is naming half the problem");

        var plan = LlmWorkflowPlanner.Deserialize(response.Json);
        plan.Subtasks.Count.ShouldBe(4, "the sibling subtasks were never evidence about each other's acceptance");
        plan.Subtasks.ShouldAllBe(subtask => subtask.Acceptance == null);
        plan.DroppedAcceptances.ShouldNotBeNull().Select(drop => drop.SubtaskId).ShouldBe(new[] { "s1", "s2", "s3", "s4" });
    }

    [Theory]
    [InlineData("Anthropic", "title")]
    [InlineData("OpenAI", "title")]
    [InlineData("Anthropic", "questions")]
    [InlineData("OpenAI", "questions")]
    public async Task A_schema_violation_the_consumer_has_no_degrade_for_is_still_a_bounded_typed_fault(string provider, string defect)
    {
        // The degrade is scoped by the CONSUMER's own verdict on a named position, not by "this looked like an
        // acceptance problem". Missing `title` faults `$.subtasks[0]` — outside the acceptance the advisory claims,
        // and a subtask with no title is not something there is anything left to degrade TO. Every reply here ALSO
        // carries a degradable acceptance, so this is the composition: one fatal defect outvotes any number of
        // advisory ones.
        //
        // The `questions` arm is the one the SCHEMA alone can see — a question with no options binds fine (the record
        // defaults the list) and the typed consumer check passes it. It is therefore what makes a blanket "this reply
        // has an advisory, so its schema errors are advisory too" attribution observable instead of merely wrong.
        var reply = defect switch
        {
            "title" => PlannerReply("TestsPass").Replace("\"title\":\"result\",", ""),
            _ => PlannerReply("TestsPass").Replace("\"successCriteria\":[]", "\"questions\":[{\"id\":\"q1\",\"question\":\"which shape?\"}],\"successCriteria\":[]"),
        };
        var handler = new WireHandler(provider, [reply, reply]);

        var error = await Should.ThrowAsync<LlmApiException>(() => Client(provider, handler).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None));

        error.Category.ShouldBe(LlmErrorCategory.Malformed);
        handler.Bodies.Count.ShouldBe(2, "the re-ask stays bounded to one");
        handler.Bodies[1].ShouldContain("did NOT conform to the required JSON Schema", customMessage: "a reply carrying anything fatal gets the fatal preamble, never the accepting one");
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task The_reask_preamble_calls_a_fatal_miss_invalid_and_never_says_that_of_a_degradable_one(string provider)
    {
        // One preamble cannot serve both severities. Telling a model "your previous (invalid) response did NOT
        // conform" about the exact reply the consumer is going to ACCEPT is a false correction, and it invites the
        // model to re-author the parts that were already right.
        var fatal = new WireHandler(provider, ["{}", "{}"]);
        await Should.ThrowAsync<LlmApiException>(() => Client(provider, fatal).CompleteStructuredAsync(Request(provider), CancellationToken.None));

        fatal.Bodies[1].ShouldContain("did NOT conform to the required JSON Schema");
        fatal.Bodies[1].ShouldContain("previous (invalid) response");

        var advisory = new WireHandler(provider, [PlannerReply("TestsPass"), PlannerReply("TestsPass")]);
        await Client(provider, advisory).CompleteStructuredAsync(PlannerRequest(provider), CancellationToken.None);

        advisory.Bodies[1].ShouldContain("left part of itself unusable by its consumer");
        advisory.Bodies[1].ShouldContain("everything else in it is accepted as authored");
        advisory.Bodies[1].ShouldNotContain("did NOT conform", customMessage: "the reply is about to be accepted; the severity is the point, not which checker noticed");
        advisory.Bodies[1].ShouldNotContain("previous (invalid) response", customMessage: "a degradable reply is not invalid; calling it that is a lie the model then acts on");
    }

    /// <summary>The live regression shape: one subtask that names an oracle kind and authors NO payload for it — a consumer-contract defect the model-visible schema ALSO faults (no per-kind <c>oneOf</c> branch matches), which is why the two must be read as one severity. <paramref name="payload"/> appends raw acceptance keys, so an EMPTY payload can be authored too.</summary>
    private static string PlannerReply(string kind, string payload = "") =>
        PlannerReplyWithAcceptance("{\"formatVersion\":2,\"kind\":\"" + kind + "\"" + payload + "}");

    /// <summary>The same one-subtask reply with an acceptance written verbatim, so an arm can post a shape the wire record itself cannot hold (a v1 <c>command</c> key).</summary>
    private static string PlannerReplyWithAcceptance(string acceptance) =>
        "{\"goal\":\"produce the requested result\",\"subtasks\":[{\"id\":\"s1\",\"title\":\"result\",\"instruction\":\"do the work\",\"acceptance\":" + acceptance + "}],\"successCriteria\":[],\"risks\":[],\"recommendedWorkflowKind\":\"coding\"}";

    /// <summary>Lane run 34093741284's reply, transcribed from its violation list: four acceptances with no <c>formatVersion</c>, three of them also missing the payload their oracle needs. The reply that made <c>planner.Status == Failure</c>.</summary>
    private const string LiveFourSubtaskReply =
        "{\"goal\":\"produce the requested result\",\"subtasks\":["
      + "{\"id\":\"s1\",\"title\":\"Judge\",\"instruction\":\"judge the writeup\",\"acceptance\":{\"kind\":\"LlmJudge\"}},"
      + "{\"id\":\"s2\",\"title\":\"Test\",\"instruction\":\"run the tests\",\"acceptance\":{\"kind\":\"TestsPass\"}},"
      + "{\"id\":\"s3\",\"title\":\"Test again\",\"instruction\":\"run the other tests\",\"acceptance\":{\"kind\":\"TestsPass\"}},"
      + "{\"id\":\"s4\",\"title\":\"Deliver\",\"instruction\":\"write the report\",\"acceptance\":{\"kind\":\"ArtifactPresent\"}}"
      + "],\"successCriteria\":[],\"risks\":[],\"recommendedWorkflowKind\":\"coding\"}";

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

        /// <summary>The HTTP status of the Nth reply, defaulting to 200 for every one it does not cover. A non-200 entry returns its queued body verbatim as the gateway's error payload, so an arm can fail a re-ask on the TRANSPORT rather than on what it authored.</summary>
        public HttpStatusCode[] Statuses { get; init; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var content = responses[Math.Min(Bodies.Count - 1, responses.Length - 1)];
            var status = Bodies.Count <= Statuses.Length ? Statuses[Bodies.Count - 1] : HttpStatusCode.OK;

            if (status != HttpStatusCode.OK) return new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

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
