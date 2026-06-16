using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// MECHANISM test for <see cref="RecordReplayStructuredLLMClient"/> — proves the record/replay decorator works
/// NOW, in CI, with no API key. The inner client here is a HAND-AUTHORED STUB (<see cref="StubInner"/>), so this
/// makes NO real-model claim: it asserts the plumbing (record writes a cassette → replay reads it back verbatim
/// → a replay miss fails loudly), not that any model authored anything. The genuine real-model claim lives in
/// <see cref="RealModelPhaseAuthorshipFlowTests"/>, which uses the REAL AnthropicClient as the inner.
///
/// <para>It runs against a TEMP cassette path (not the committed <c>Cassettes/</c> dir) so it never mutates a
/// real transcript and leaves no artefact behind (Rule 12.3 cleanup). Tagged Integration so it runs in the same
/// CI gate that builds this project (a Unit trait here would run in neither gate — same rationale as
/// <see cref="SubtaskAwareFakeCliDriftTests"/>).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class RecordReplayStructuredLLMClientTests : IDisposable
{
    private readonly string _cassettePath = Path.Combine(Path.GetTempPath(), $"cs-recordreplay-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_cassettePath); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Record_then_replay_round_trips_the_inner_completion_verbatim()
    {
        var request = MakeRequest("decompose: build a login page");
        var inner = new StubInner(JsonSerializer.SerializeToElement(new { subtasks = new[] { "form", "validation", "submit" } }), "stub-model-1");

        // RECORD: delegates to the stub + persists the cassette.
        var recorded = await RecordReplayStructuredLLMClient.ForRecording(inner, _cassettePath).CompleteStructuredAsync(request, CancellationToken.None);

        File.Exists(_cassettePath).ShouldBeTrue("record mode must persist a cassette file");
        inner.CallCount.ShouldBe(1, "record mode delegates to the inner client exactly once");

        // REPLAY: a fresh decorator (no inner) serves the SAME request from the committed cassette, no inner call.
        var replayed = await RecordReplayStructuredLLMClient.ForReplay(_cassettePath).CompleteStructuredAsync(request, CancellationToken.None);

        replayed.Model.ShouldBe(recorded.Model, "replay returns the recorded model verbatim");
        replayed.Json.GetRawText().ShouldBe(recorded.Json.GetRawText(), "replay returns the recorded JSON verbatim — byte-for-byte the inner's output");
    }

    [Fact]
    public async Task Replay_miss_throws_a_clear_error_and_never_fabricates()
    {
        var recordedRequest = MakeRequest("decompose: build a login page");
        var inner = new StubInner(JsonSerializer.SerializeToElement(new { subtasks = new[] { "a" } }), "stub-model-1");

        await RecordReplayStructuredLLMClient.ForRecording(inner, _cassettePath).CompleteStructuredAsync(recordedRequest, CancellationToken.None);

        // A DIFFERENT prompt hashes to a different key → a miss against the cassette we just wrote.
        var missRequest = MakeRequest("decompose: a totally different task that was never recorded");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            RecordReplayStructuredLLMClient.ForReplay(_cassettePath).CompleteStructuredAsync(missRequest, CancellationToken.None));

        ex.Message.ShouldContain("cassette MISS", customMessage: "a miss must fail loudly so a stale/missing cassette is visible — never silently green");
        ex.Message.ShouldContain(RecordReplayStructuredLLMClient.CassetteKey(missRequest), customMessage: "the error names the missing key so a human can diagnose + re-record");
    }

    [Fact]
    public async Task Re_recording_an_existing_key_overwrites_in_place()
    {
        var request = MakeRequest("decompose: stable prompt");

        var first = new StubInner(JsonSerializer.SerializeToElement(new { subtasks = new[] { "v1" } }), "model-v1");
        await RecordReplayStructuredLLMClient.ForRecording(first, _cassettePath).CompleteStructuredAsync(request, CancellationToken.None);

        var second = new StubInner(JsonSerializer.SerializeToElement(new { subtasks = new[] { "v2", "v2b" } }), "model-v2");
        await RecordReplayStructuredLLMClient.ForRecording(second, _cassettePath).CompleteStructuredAsync(request, CancellationToken.None);

        var replayed = await RecordReplayStructuredLLMClient.ForReplay(_cassettePath).CompleteStructuredAsync(request, CancellationToken.None);

        replayed.Model.ShouldBe("model-v2", "re-recording the same key replaces the prior recording — a human re-running the live test refreshes in place");
        replayed.Json.GetProperty("subtasks").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void CassetteKey_is_stable_and_schema_property_order_insensitive()
    {
        var schemaA = JsonSerializer.SerializeToElement(new { type = "object", properties = new { subtasks = new { type = "array" } }, required = new[] { "subtasks" } });
        var schemaB = JsonSerializer.SerializeToElement(new { required = new[] { "subtasks" }, properties = new { subtasks = new { type = "array" } }, type = "object" });

        var keyA = RecordReplayStructuredLLMClient.CassetteKey(MakeRequest("same prompt", schemaA));
        var keyB = RecordReplayStructuredLLMClient.CassetteKey(MakeRequest("same prompt", schemaB));

        keyA.ShouldBe(keyB, "the key canonicalizes schema property order, so a reordered-but-equal schema hashes identically");

        var keyDifferentPrompt = RecordReplayStructuredLLMClient.CassetteKey(MakeRequest("DIFFERENT prompt", schemaA));
        keyDifferentPrompt.ShouldNotBe(keyA, "a real prompt change MUST move the key — that's what makes the drift detector work");
    }

    private static StructuredLLMCompletionRequest MakeRequest(string userPrompt, JsonElement? schema = null) => new()
    {
        Model = "claude-sonnet-4-5",
        SystemPrompt = "",
        UserPrompt = userPrompt,
        JsonSchema = schema ?? JsonSerializer.SerializeToElement(new { type = "object", properties = new { subtasks = new { type = "array", items = new { type = "string" } } }, required = new[] { "subtasks" } }),
    };

    /// <summary>A deterministic hand-authored inner — NOT a model. Returns a fixed completion and counts calls so the test can assert record delegates exactly once.</summary>
    private sealed class StubInner : IStructuredLLMClient
    {
        private readonly JsonElement _json;
        private readonly string _model;

        public StubInner(JsonElement json, string model) { _json = json; _model = model; }

        public int CallCount { get; private set; }

        public string Provider => "Stub";

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new StructuredLLMCompletion { Json = _json.Clone(), Model = _model });
        }
    }
}
