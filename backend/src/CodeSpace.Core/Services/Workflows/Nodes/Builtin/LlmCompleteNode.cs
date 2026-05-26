using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Single-turn LLM completion. Config picks <c>provider</c> (Anthropic, etc) + <c>model</c>;
/// inputs supply the rendered system + user prompts. Outputs the completion text + token
/// counts. Two distinct knobs:
///   - <see cref="NodeManifest.ConfigSchema"/> — "which LLM" (rarely changes at runtime)
///   - <see cref="NodeManifest.InputSchema"/> — "what to say" (almost always {{ref}}'d from upstream node outputs)
/// This split is what makes the AI Code Review template "drop in YOUR repo" without editing
/// the LLM config — the user only edits the trigger and the prompt inputs.
/// </summary>
public sealed class LlmCompleteNode : INodeRuntime
{
    private readonly ILLMClientRegistry _clientRegistry;

    public LlmCompleteNode(ILLMClientRegistry clientRegistry) { _clientRegistry = clientRegistry; }

    public string TypeKey => "llm.complete";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "LLM completion",
        Category = "AI",
        Kind = NodeKind.Regular,
        IconKey = "sparkles",
        Description = "Calls an LLM with a system + user prompt and returns the completion text.",
        // LLM completions have BILLING side effects: every call is metered. A retry that
        // re-bills is not "duplicate effect on the world" in the same way as a PR comment,
        // but the operator still doesn't want to be charged twice when the engine recovers
        // from a worker crash. Mark side-effecting.
        IsSideEffecting = true,
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "provider": { "type": "string", "enum": ["Anthropic"], "default": "Anthropic" },
                "model": { "type": "string" },
                "maxTokens": { "type": "integer", "minimum": 1, "maximum": 8192, "default": 2048 },
                "temperature": { "type": "number", "minimum": 0, "maximum": 1, "default": 0.2 }
              },
              "required": ["provider"]
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "systemPrompt": { "type": "string" },
                "userPrompt": { "type": "string", "minLength": 1 }
              },
              "required": ["userPrompt"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "text": { "type": "string" },
                "model": { "type": "string" },
                "inputTokens": { "type": ["integer","null"] },
                "outputTokens": { "type": ["integer","null"] }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var provider = ReadString(context.Config, "provider", "Anthropic");
        var model = ReadString(context.Config, "model", DefaultModelFor(provider));
        var maxTokens = ReadInt(context.Config, "maxTokens", 2048);
        var temperature = ReadDouble(context.Config, "temperature", 0.2);

        var systemPrompt = ReadString(context.Inputs, "systemPrompt", "");
        var userPrompt = ReadString(context.Inputs, "userPrompt", "");

        if (string.IsNullOrWhiteSpace(userPrompt)) return NodeResult.Fail("Input 'userPrompt' is required.");

        var client = _clientRegistry.Resolve(provider);

        // Wrap the LLM call with ledger emission. The completed record's response_payload
        // carries the token counts (cheap to inline). The full text lives in workflow_artifact,
        // keeping the ledger payload small while still preserving the model output for replay
        // / audit.
        var completion = await context.Observability.TraceExternalCallAsync(
            target: $"{provider.ToLowerInvariant()}:{model}",
            method: "complete",
            requestPayload: BuildRequestPayloadAudit(model, systemPrompt, userPrompt, maxTokens, temperature),
            action: ct => client.CompleteAsync(new LLMCompletionRequest
            {
                Model = model,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                MaxOutputTokens = maxTokens,
                Temperature = temperature
            }, ct),
            completionExtractor: result => new ExternalCallCompletion
            {
                ResponsePayload = JsonSerializer.SerializeToElement(new
                {
                    model = result.Model,
                    input_tokens = result.InputTokens,
                    output_tokens = result.OutputTokens,
                    text_length = result.Text?.Length ?? 0,
                })
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        context.Logger.LogInformation("LLM completion {Model} in={InTok} out={OutTok}", completion.Model, completion.InputTokens, completion.OutputTokens);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["text"] = JsonSerializer.SerializeToElement(completion.Text),
            ["model"] = JsonSerializer.SerializeToElement(completion.Model),
            ["inputTokens"] = JsonSerializer.SerializeToElement(completion.InputTokens),
            ["outputTokens"] = JsonSerializer.SerializeToElement(completion.OutputTokens)
        };

        return NodeResult.Ok(outputs);
    }

    /// <summary>
    /// Build the redacted summary of the LLM request for the ledger. We persist counts +
    /// model, NOT the prompts themselves — prompts can carry tenant data and are kept in the
    /// engine's resolved-inputs path (already redacted upstream) instead of the call-side
    /// audit. Total prompt characters is enough to triage "why was this call so expensive".
    /// </summary>
    private static JsonElement BuildRequestPayloadAudit(string model, string systemPrompt, string userPrompt, int maxTokens, double temperature) =>
        JsonSerializer.SerializeToElement(new
        {
            model,
            max_tokens = maxTokens,
            temperature,
            system_prompt_chars = systemPrompt?.Length ?? 0,
            user_prompt_chars = userPrompt?.Length ?? 0,
        });

    private static string DefaultModelFor(string provider) => provider switch
    {
        "Anthropic" => "claude-sonnet-4-5",
        _ => "default"
    };

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key, string fallback)
    {
        if (!bag.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.String) return fallback;
        return value.GetString() ?? fallback;
    }

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> bag, string key, int fallback)
    {
        if (!bag.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number) return fallback;
        return value.TryGetInt32(out var i) ? i : fallback;
    }

    private static double ReadDouble(IReadOnlyDictionary<string, JsonElement> bag, string key, double fallback)
    {
        if (!bag.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number) return fallback;
        return value.TryGetDouble(out var d) ? d : fallback;
    }
}
