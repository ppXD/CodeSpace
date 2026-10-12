using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A streaming model that prints back what it was told to, paced so the echo straddles the recorder's 32-KiB delta
/// flush: everything after <see cref="EchoMarker"/> in the user prompt is streamed with only its first ten characters
/// before the size bound and the rest after it. This is the prompt-injected model that splits a secret across two
/// <c>interaction.delta</c> rows on purpose. After <see cref="HoldMarker"/> instead, it streams only the start of the
/// value and holds the stream open until the call is cancelled: the model an operator cancels mid-secret. Registered
/// under its own provider tag, so it sits beside every other fake without a collision, and through the production
/// recording decorators like any real client.
/// </summary>
public sealed class SecretEchoStreamingLlmClient : ILLMClient, IStructuredLLMClient, IStreamingLLMClient
{
    public const string ProviderTag = "TestSecretEchoStream";

    public const string EchoMarker = "ECHO:";

    public const string HoldMarker = "HOLD:";

    /// <summary>What a held stream sends before the start of the value: text no secret can begin with, so a flush can settle it.</summary>
    public const string HeldLead = "progress ";

    /// <summary>How much of the value a held stream sends before it holds: the start of it, never the whole.</summary>
    public const int HeldValueChars = 14;

    /// <summary>The filler streamed before the echo, sized so the first ten echoed characters complete the flush bound.</summary>
    public static readonly string Filler = new('x', RecordingStreamingStructuredLLMClientDecorator.DeltaFlushBytes - 10);

    public string Provider => ProviderTag;

    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LLMCompletion { Text = Filler + EchoOf(request.UserPrompt), Model = request.Model, Usage = new() { InputTokens = 1, OutputTokens = 1, FinishReason = "stop" } });

    public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(new { }), Model = request.Model });

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request.UserPrompt.Contains(HoldMarker, StringComparison.Ordinal))
        {
            yield return new LlmStreamEvent.Meta(Model: request.Model, InputTokens: 7);
            yield return new LlmStreamEvent.TextDelta(HeldLead + After(request.UserPrompt, HoldMarker)[..HeldValueChars]);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        var echo = EchoOf(request.UserPrompt);

        yield return new LlmStreamEvent.Meta(Model: request.Model, InputTokens: 7);
        yield return new LlmStreamEvent.TextDelta(Filler + echo[..10]);
        await Task.Yield();
        yield return new LlmStreamEvent.TextDelta(echo[10..] + " done");
        yield return new LlmStreamEvent.Meta(OutputTokens: 4, FinishReason: "end_turn");
    }

    private static string EchoOf(string userPrompt) => After(userPrompt, EchoMarker);

    private static string After(string userPrompt, string marker) => userPrompt[(userPrompt.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
}
