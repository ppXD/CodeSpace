using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Messages.Tasks;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Tasks.SpecPreview;

public interface ITaskSpecCompiler
{
    /// <summary>Compile a free-text goal into launch-contract suggestions. Never throws for a model-path miss — a null <c>Suggestion</c> is the honest degrade (the composer shows nothing).</summary>
    Task<CompileTaskSpecResult> CompileAsync(Guid teamId, string goal, Guid? repositoryId, CancellationToken cancellationToken);
}

/// <summary>
/// P5-7 (I1 spec compiler, first slice) — compile a prose goal into TYPED suggestions for the launch surface's
/// EXISTING fields: an executable acceptance argv, definition-of-done criteria, a delivery preference. Default-ON
/// with no flag (owner ruling 2026-07-26; the no-env-toggle discipline): availability is governed by the same
/// thing every launch-time model call is governed by — whether the team has a structured-capable pool model.
///
/// <para><b>兜底 (graceful degradation), the <see cref="Effort.Classifiers.Llm.LlmEffortClassifier"/> posture:</b>
/// no structured provider, no pool model, a transport/gateway fault, a malformed reply — every miss returns a
/// null suggestion, never a throw: the preview is a best-effort enhancement and must never break the launch
/// composer. Grounding is team-scoped and fail-soft (the same <see cref="IRepoGroundingProvider"/> seam the
/// planner uses, reference: null — a pin belongs to a RUN, not to a pre-launch preview).</para>
///
/// <para><b>Authority by construction:</b> the reply is validated (<see cref="AgentAcceptanceContract.ValidateAuthored"/> —
/// a check that fails authoring validation is DROPPED with an honest rationale note, never handed to the operator
/// broken) and returned as plain suggestions. Nothing is persisted, nothing is staked, no authority is minted:
/// whatever the operator keeps arrives on ordinary <c>LaunchTaskCommand</c> fields and stakes as Operator via the
/// P5-4 provenance carrier — the model's output cannot reach the ledger except through the operator's own submit.</para>
/// </summary>
public sealed class TaskSpecCompiler : ITaskSpecCompiler, IScopedDependency
{
    private readonly ILLMClientRegistry _clients;
    private readonly IModelPoolSelector _models;
    private readonly IRepoGroundingProvider _grounding;
    private readonly ILogger<TaskSpecCompiler> _logger;

    public TaskSpecCompiler(ILLMClientRegistry clients, IModelPoolSelector models, IRepoGroundingProvider grounding, ILogger<TaskSpecCompiler> logger)
    {
        _clients = clients;
        _models = models;
        _grounding = grounding;
        _logger = logger;
    }

    public async Task<CompileTaskSpecResult> CompileAsync(Guid teamId, string goal, Guid? repositoryId, CancellationToken cancellationToken)
    {
        var grounding = await BuildGroundingAsync(teamId, repositoryId, cancellationToken).ConfigureAwait(false);

        var compilation = await TryCompileWithModelAsync(teamId, goal, grounding, cancellationToken).ConfigureAwait(false);
        var suggestion = compilation is null ? null : ToSuggestion(compilation);

        // The degrade is BY DESIGN indistinguishable from "nothing to suggest" on the wire — so the log must be
        // the place an operator can tell WHICH arm fired (no model, model fault, model replied empty, or a real
        // suggestion). One line per compile, never per-token.
        if (suggestion is null)
            _logger.LogInformation("Spec preview compiled NOTHING for team {TeamId}: {Reason} (grounded={Grounded})", teamId, compilation is null ? "model path missed (see preceding warning)" : "the model replied but mapped empty (no checks, no criteria, no delivery opinion)", grounding is not null);
        else
            _logger.LogInformation("Spec preview compiled for team {TeamId}: checks={Checks}, criteria={Criteria}, delivery={Delivery}, confidence={Confidence:0.00}, grounded={Grounded}", teamId, suggestion.AcceptanceChecks.Count, suggestion.AcceptanceCriteria.Count, suggestion.OpenPullRequest?.ToString() ?? "none", suggestion.Confidence, grounding is not null);

        return new CompileTaskSpecResult { Suggestion = suggestion, Grounded = grounding is not null };
    }

    /// <summary>Grounding is itself best-effort — a grounding fault degrades to an ungrounded compile, never a failed preview.</summary>
    private async Task<string?> BuildGroundingAsync(Guid teamId, Guid? repositoryId, CancellationToken cancellationToken)
    {
        if (repositoryId is null) return null;

        try
        {
            return await _grounding.BuildGroundingAsync(repositoryId, teamId, reference: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spec preview grounding failed for team {TeamId}; compiling ungrounded", teamId);
            return null;
        }
    }

    /// <summary>The model's compilation, or null on ANY model-path miss (no provider/pool model, keyless credential, transport fault, malformed reply) — the documented 兜底.</summary>
    private async Task<TaskSpecCompilation?> TryCompileWithModelAsync(Guid teamId, string goal, string? grounding, CancellationToken cancellationToken)
    {
        try
        {
            if (await InProcessStructuredModel.ResolveAsync(_clients, _models, teamId, cancellationToken).ConfigureAwait(false) is not { } resolved)
                return null;

            var (structured, pick) = resolved;

            var completion = await structured.CompleteStructuredAsync(BuildRequest(goal, grounding, pick), cancellationToken).ConfigureAwait(false);

            return completion.Json.Deserialize<TaskSpecCompilation>(TaskSpecCompilerSchema.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spec preview model path missed for team {TeamId}; the preview degrades to no suggestion", teamId);
            return null;
        }
    }

    private static StructuredLLMCompletionRequest BuildRequest(string goal, string? grounding, ModelPoolPick pick) => new()
    {
        Model = pick.ModelId,
        Credential = pick.Credential,
        SystemPrompt = SystemPrompt,
        UserPrompt = grounding is null ? $"Goal to compile:\n{goal}" : $"Repository layout (ground truth — suggest a check ONLY if this shows its toolchain):\n{grounding}\n\nGoal to compile:\n{goal}",
        JsonSchema = TaskSpecCompilerSchema.ResponseSchema,
        MaxOutputTokens = 1024,
        Temperature = 0.0,
    };

    private const string SystemPrompt =
        "You compile a free-text engineering goal into launch-contract suggestions an operator will review and edit. " +
        "Suggest an EXECUTABLE acceptance check (argv tokens) only when the repository layout shows its toolchain exists — a wrong check is worse than none. " +
        "Criteria are crisp, verifiable definition-of-done bullets, not restatements. " +
        "Claim a delivery opinion only when the goal actually expresses one. Reply with ONLY the schema JSON.";

    /// <summary>
    /// Map the reply into the wire suggestion (pure; unit-pinned). The v1 shape is valid-by-construction against
    /// the shared authoring rule (whitespace argv is pre-filtered here; no rubric/schema kinds are ever suggested,
    /// so <c>AgentAcceptanceContract.ValidateAuthored</c> has no reachable failure) — and the launch pipeline
    /// re-validates whatever the operator finally submits regardless. A delivery opinion exists ONLY when the
    /// model explicitly claimed one (never invented); an entirely empty reply maps to null (the card renders
    /// nothing, never an empty scaffold).
    /// </summary>
    internal static TaskSpecSuggestion? ToSuggestion(TaskSpecCompilation compilation)
    {
        var checks = compilation.AcceptanceChecks.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        var criteria = compilation.AcceptanceCriteria.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var openPullRequest = compilation.HasDeliveryOpinion ? compilation.OpenPullRequest : (bool?)null;
        var targetBranch = compilation.HasDeliveryOpinion && !string.IsNullOrWhiteSpace(compilation.TargetBranch) ? compilation.TargetBranch.Trim() : null;

        if (checks.Count == 0 && criteria.Count == 0 && openPullRequest is null) return null;

        return new TaskSpecSuggestion
        {
            AcceptanceChecks = checks,
            AcceptanceCriteria = criteria,
            OpenPullRequest = openPullRequest,
            TargetBranch = targetBranch,
            Rationale = string.IsNullOrWhiteSpace(compilation.Rationale) ? "Compiled from the goal." : compilation.Rationale.Trim(),
            Confidence = Math.Clamp(compilation.Confidence, 0.0, 1.0),
        };
    }
}
