using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Test-only RECORD/REPLAY decorator over an <see cref="IStructuredLLMClient"/> — the instrument that lets a
/// SINGLE integration test exercise a REAL model authoring the plan-map-synth subtask decomposition while CI
/// (no key) stays deterministic. It is NOT production code and is NOT auto-registered: a test wraps a chosen
/// inner client (the real <c>AnthropicClient</c> for the kill-gate, a hand-authored stub for the mechanism
/// test) and retargets the planner node at this client's <see cref="Provider"/> tag.
///
/// <para><b>RECORD</b> (active only when the inner client can actually reach a model — i.e. a real API key is
/// present): delegate <see cref="CompleteStructuredAsync"/> to the inner client, then persist the
/// <see cref="CassetteKey"/> → (model, json) into the committed cassette file. Re-recording an existing key
/// overwrites it, so a human re-running the live test refreshes the transcript in place.</para>
///
/// <para><b>REPLAY</b> (default — no key, CI): look the key up in the loaded cassette. A HIT returns the
/// recorded completion verbatim; a MISS throws a CLEAR descriptive exception. A miss NEVER fabricates a
/// completion — a stale or missing cassette MUST fail loudly so it is visible, never silently green.</para>
///
/// <para><b>The cassette key</b> is a stable SHA-256 over (Model, SystemPrompt, UserPrompt, canonicalized
/// JsonSchema). Deterministic across runs and machines, and SENSITIVE to a real prompt/schema change — which
/// is what lets <c>PlannerCassetteDriftTests</c> detect that a planner edit invalidated the recording.</para>
/// </summary>
public sealed class RecordReplayStructuredLLMClient : ILLMClient, IStructuredLLMClient
{
    /// <summary>The provider tag a test retargets the planner node at to route through THIS decorator. Distinct from "Anthropic" so it coexists with the real client in the registry without a duplicate-provider collision when both are present.</summary>
    public const string ProviderTag = "RecordReplay";

    private static readonly JsonSerializerOptions CassetteJson = new() { WriteIndented = true };

    private readonly IStructuredLLMClient? _inner;
    private readonly string _cassettePath;
    private readonly bool _recordMode;
    private readonly List<CassetteEntry> _entries;

    private RecordReplayStructuredLLMClient(IStructuredLLMClient? inner, string cassettePath, bool recordMode, List<CassetteEntry> entries)
    {
        _inner = inner;
        _cassettePath = cassettePath;
        _recordMode = recordMode;
        _entries = entries;
    }

    public string Provider => ProviderTag;

    /// <summary>The planner only ever calls the structured path — this decorator is structured-only by design. A free-text call is a wiring mistake, surfaced loudly rather than silently delegated.</summary>
    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{nameof(RecordReplayStructuredLLMClient)} serves the structured path only — use {nameof(CompleteStructuredAsync)}.");

    /// <summary>RECORD: delegate to the inner real client + persist. The inner MUST be non-null (a recordable client was supplied).</summary>
    public static RecordReplayStructuredLLMClient ForRecording(IStructuredLLMClient inner, string cassettePath)
    {
        if (inner == null)
            throw new ArgumentNullException(nameof(inner), "RECORD mode needs a real inner client to delegate to.");

        return new RecordReplayStructuredLLMClient(inner, cassettePath, recordMode: true, LoadEntries(cassettePath));
    }

    /// <summary>REPLAY: serve from the committed cassette; no inner client is consulted. A miss throws.</summary>
    public static RecordReplayStructuredLLMClient ForReplay(string cassettePath) =>
        new(inner: null, cassettePath, recordMode: false, LoadEntries(cassettePath));

    /// <summary>True when a committed cassette file exists at <paramref name="cassettePath"/> (gates the replay test).</summary>
    public static bool CassetteExists(string cassettePath) => File.Exists(cassettePath);

    /// <summary>The stable hash key for a request — deterministic across runs, sensitive to a prompt/schema change.</summary>
    public static string CassetteKey(StructuredLLMCompletionRequest request)
    {
        // ' ' separator so concatenated fields can't collide (e.g. "ab"+"c" vs "a"+"bc").
        var canonical = string.Join(' ', request.Model, request.SystemPrompt, request.UserPrompt, CanonicalizeSchema(request.JsonSchema));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var key = CassetteKey(request);

        if (_recordMode) return await RecordAsync(request, key, cancellationToken).ConfigureAwait(false);

        return Replay(request, key);
    }

    private async Task<StructuredLLMCompletion> RecordAsync(StructuredLLMCompletionRequest request, string key, CancellationToken cancellationToken)
    {
        var completion = await _inner!.CompleteStructuredAsync(request, cancellationToken).ConfigureAwait(false);

        Persist(key, request, completion);

        return completion;
    }

    private StructuredLLMCompletion Replay(StructuredLLMCompletionRequest request, string key)
    {
        var entry = _entries.FirstOrDefault(e => e.KeyHash == key);

        if (entry == null)
            throw new InvalidOperationException(
                $"RecordReplay cassette MISS for key {key} (model='{request.Model}', userPrompt='{Preview(request.UserPrompt)}'). " +
                $"The cassette at '{_cassettePath}' has no recording for this request — it is missing or stale. " +
                $"Re-record by running the RealModel-tagged live test with {Core.Services.Workflows.Llm.Anthropic.AnthropicClient.ApiKeyEnvVar} set, then commit the updated cassette.");

        return new StructuredLLMCompletion { Json = entry.JsonElement(), Model = entry.Model };
    }

    private void Persist(string key, StructuredLLMCompletionRequest request, StructuredLLMCompletion completion)
    {
        _entries.RemoveAll(e => e.KeyHash == key);

        _entries.Add(new CassetteEntry
        {
            KeyHash = key,
            Model = completion.Model,
            SystemPromptPreview = Preview(request.SystemPrompt),
            UserPromptPreview = Preview(request.UserPrompt),
            Json = completion.Json.GetRawText(),
        });

        Directory.CreateDirectory(Path.GetDirectoryName(_cassettePath)!);
        File.WriteAllText(_cassettePath, JsonSerializer.Serialize(_entries.OrderBy(e => e.KeyHash).ToList(), CassetteJson));
    }

    private static List<CassetteEntry> LoadEntries(string cassettePath)
    {
        if (!File.Exists(cassettePath)) return new List<CassetteEntry>();

        var json = File.ReadAllText(cassettePath);

        return JsonSerializer.Deserialize<List<CassetteEntry>>(json) ?? new List<CassetteEntry>();
    }

    /// <summary>Re-serialize the schema through a sorted-property canonical form so semantically-equal schemas hash identically regardless of property ordering or whitespace.</summary>
    private static string CanonicalizeSchema(JsonElement schema)
    {
        using var doc = JsonDocument.Parse(schema.GetRawText());
        return Canonicalize(doc.RootElement);
    }

    private static string Canonicalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonicalize(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonicalize)) + "]",
        _ => element.GetRawText(),
    };

    private static string Preview(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= 120 ? value : value[..120] + "…";
    }

    /// <summary>One human-diffable cassette row. The previews exist for review legibility; only <see cref="KeyHash"/>, <see cref="Model"/>, and <see cref="Json"/> are load-bearing on replay.</summary>
    public sealed record CassetteEntry
    {
        public required string KeyHash { get; init; }
        public required string Model { get; init; }
        public string? SystemPromptPreview { get; init; }
        public string? UserPromptPreview { get; init; }
        public required string Json { get; init; }

        public JsonElement JsonElement() => JsonDocument.Parse(Json).RootElement.Clone();
    }
}
