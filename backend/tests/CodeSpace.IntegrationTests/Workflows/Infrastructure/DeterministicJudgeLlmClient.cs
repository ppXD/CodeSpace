using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The HONEST structured fake for the RUBRIC JUDGE flows (triad S7) — the judge sibling of
/// <see cref="DeterministicCriticLlmClient"/>. The grader runs the production <c>LlmRubricJudge</c> (real pool
/// resolve, real schema-constrained call, real complete-echo projection); only the network call is replaced. Its
/// verdict is a PURE FUNCTION of the artifact under judgment: it parses the rubric lines (<c>- [id] requirement</c>)
/// out of the prompt and answers each criterion met=true IFF the prompt carries the deliverable marker
/// <c>MEETS[id]</c> — so a revision that actually adds the required content flips the verdict, and one that doesn't
/// can't. Every criterion is echoed (the production projection fails closed on an incomplete echo, and THAT path is
/// covered by unit tests, not by this fake misbehaving).
/// </summary>
public sealed partial class DeterministicJudgeLlmClient : ILLMClient, IStructuredLLMClient
{
    /// <summary>Distinct provider tag — sits beside the other fakes + the real client with no duplicate-provider collision.</summary>
    public const string ProviderTag = "TestJudge";

    /// <summary>The deliverable marker that makes criterion <c>id</c> met: <c>MEETS[id]</c> in the judged artifact.</summary>
    public static string MeetsMarker(string criterionId) => $"MEETS[{criterionId}]";

    public string Provider => ProviderTag;

    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LLMCompletion { Text = "judged", Model = request.Model, Usage = new() { InputTokens = 2, OutputTokens = 2 } });

    public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var criteria = RubricLine().Matches(request.UserPrompt)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Select(id => new { id, met = request.UserPrompt.Contains(MeetsMarker(id), StringComparison.Ordinal), evidence = $"marker MEETS[{id}] {(request.UserPrompt.Contains(MeetsMarker(id), StringComparison.Ordinal) ? "present" : "absent")}" })
            .ToArray();

        var json = JsonSerializer.SerializeToElement(new { criteria });

        return Task.FromResult(new StructuredLLMCompletion { Json = json, Model = request.Model, Usage = new() { InputTokens = 9, OutputTokens = 5 } });
    }

    /// <summary>A rubric line as the production prompt renders it: <c>- [id] requirement</c>.</summary>
    [GeneratedRegex(@"^- \[([^\]]+)\]", RegexOptions.Multiline)]
    private static partial Regex RubricLine();
}
