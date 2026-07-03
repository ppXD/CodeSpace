using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Review;

/// <summary>
/// The model-backed <see cref="IRubricJudge"/> (triad S7) — mirrors <see cref="LlmStructuredCritic"/>'s
/// independent-brain call exactly (pinned judge row, else the team's auto-picked brain → provider-matched structured
/// client → schema-constrained completion) but answers a RUBRIC: one BINARY met/not-met + evidence per criterion,
/// temperature 0. NEVER throws (cancellation aside) — every failure returns <see cref="RubricJudgeVerdict.JudgeFailed"/>,
/// and an INCOMPLETE echo (a criterion the judge skipped or invented) is a failure too: a verdict that doesn't cover
/// the rubric is not a verdict, and fail-closed beats guessing the missing half.
/// </summary>
public sealed class LlmRubricJudge : IRubricJudge, IScopedDependency
{
    private readonly ILLMClientRegistry _clientRegistry;
    private readonly IModelPoolSelector _modelSelector;

    public LlmRubricJudge(ILLMClientRegistry clientRegistry, IModelPoolSelector modelSelector)
    {
        _clientRegistry = clientRegistry;
        _modelSelector = modelSelector;
    }

    public async Task<RubricJudgeVerdict> JudgeAsync(AcceptanceRubric rubric, string artifact, string? goal, Guid teamId, CancellationToken cancellationToken)
    {
        try
        {
            var rowId = rubric.JudgeModelId ?? await ResolveAutoJudgeAsync(teamId, cancellationToken).ConfigureAwait(false);

            if (rowId is not { } id) return RubricJudgeVerdict.JudgeFailed("no-judge-model: the team's pool has no structured-eligible model");

            var pick = await _modelSelector.ResolveByRowIdAsync(teamId, id, cancellationToken).ConfigureAwait(false);

            if (pick == null) return RubricJudgeVerdict.JudgeFailed("no-judge-model: the judge model row is not available in the team's pool");

            var structured = _clientRegistry.All.OfType<IStructuredLLMClient>().FirstOrDefault(c => string.Equals(c.Provider, pick.Credential.Provider, StringComparison.OrdinalIgnoreCase));

            if (structured == null) return RubricJudgeVerdict.JudgeFailed("no-judge-model: no structured-output provider for the judge model");

            var completion = await structured.CompleteStructuredAsync(BuildRequest(rubric, artifact, goal, pick), cancellationToken).ConfigureAwait(false);

            return Project(rubric, completion.Json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RubricJudgeVerdict.JudgeFailed($"judge-error: {ex.Message}");
        }
    }

    /// <summary>The judge auto-pick: the team's strongest structured-eligible brain (the same selector the critics use). Producer independence is moot here — the artifact's producer row isn't known at grade time, and the judge answers narrow evidence-backed questions rather than re-doing the work.</summary>
    private async Task<Guid?> ResolveAutoJudgeAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var providers = _clientRegistry.All.OfType<IStructuredLLMClient>().Select(c => c.Provider).ToList();

        return providers.Count == 0 ? null : await _modelSelector.SelectBrainRowIdAsync(teamId, providers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Project the schema-valid judge output onto the canonical verdict, joined by criterion id and FAIL-CLOSED on an incomplete echo. Internal for direct unit testing.</summary>
    internal static RubricJudgeVerdict Project(AcceptanceRubric rubric, JsonElement json)
    {
        var model = json.Deserialize<JudgeModelVerdict>(CriticSchema.Options);

        if (model?.Criteria is not { Count: > 0 } echoed) return RubricJudgeVerdict.JudgeFailed("judge-incomplete: the judge returned no criterion verdicts");

        var byId = new Dictionary<string, RubricCriterionVerdict>(StringComparer.Ordinal);

        foreach (var c in echoed)
            if (!string.IsNullOrWhiteSpace(c.Id) && !byId.ContainsKey(c.Id))
                byId[c.Id] = new RubricCriterionVerdict { Id = c.Id, Met = c.Met, Evidence = c.Evidence ?? "" };

        var missing = rubric.Criteria.Select(c => c.Id).Where(id => !byId.ContainsKey(id)).ToList();

        if (missing.Count > 0) return RubricJudgeVerdict.JudgeFailed($"judge-incomplete: no verdict for criteria [{string.Join(", ", missing)}]");

        // Order + filter by the RUBRIC (invented ids are dropped) — the verdict answers the contract, nothing else.
        return new RubricJudgeVerdict { Criteria = rubric.Criteria.Select(c => byId[c.Id]).ToList() };
    }

    private static StructuredLLMCompletionRequest BuildRequest(AcceptanceRubric rubric, string artifact, string? goal, ModelPoolPick pick) => new()
    {
        Model = pick.ModelId,
        SystemPrompt = SystemPrompt,
        UserPrompt = BuildUserPrompt(rubric, artifact, goal),
        JsonSchema = RubricVerdictSchema,
        MaxOutputTokens = 2048,
        Temperature = 0,
        Credential = pick.Credential,
    };

    /// <summary>Internal test accessor — pins the prompt framing without a model round-trip.</summary>
    internal static string BuildUserPromptForTest(AcceptanceRubric rubric, string artifact, string? goal) => BuildUserPrompt(rubric, artifact, goal);

    private static string BuildUserPrompt(AcceptanceRubric rubric, string artifact, string? goal)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(goal))
        {
            builder.AppendLine("Goal the deliverable should serve:");
            builder.AppendLine(goal);
            builder.AppendLine();
        }

        builder.AppendLine("The deliverable under judgment:");
        builder.AppendLine(artifact);
        builder.AppendLine();
        builder.AppendLine("Rubric — judge EACH criterion independently, strictly on the deliverable's own content:");
        foreach (var c in rubric.Criteria) builder.AppendLine($"- [{c.Id}] {c.Requirement}");
        builder.AppendLine();
        builder.AppendLine("For every criterion return met=true ONLY when the deliverable clearly satisfies it, with the evidence quoted or precisely located; met=false otherwise, with what is missing. Return ONLY the schema-constrained JSON with one entry per criterion id.");

        return builder.ToString();
    }

    private const string SystemPrompt =
        "You are an INDEPENDENT judge grading a deliverable against a fixed rubric. You did not write it. Judge each " +
        "criterion in isolation, strictly and literally, on the deliverable's own content — never on plausibility or " +
        "effort. Binary verdicts only: met or not met, each backed by concrete evidence from the deliverable. Return " +
        "ONLY the schema-constrained JSON.";

    /// <summary>The judge's output contract: one binary verdict + evidence per criterion id. Internal so tests pin it.</summary>
    internal static readonly JsonElement RubricVerdictSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "criteria": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "id": { "type": "string" },
                  "met": { "type": "boolean" },
                  "evidence": { "type": "string" }
                },
                "required": ["id", "met", "evidence"]
              }
            }
          },
          "required": ["criteria"]
        }
        """).RootElement.Clone();

    private sealed record JudgeModelVerdict(IReadOnlyList<JudgeModelCriterion>? Criteria);

    private sealed record JudgeModelCriterion(string? Id, bool Met, string? Evidence);
}
