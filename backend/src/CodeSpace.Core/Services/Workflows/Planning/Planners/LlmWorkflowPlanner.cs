using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Planning.Planners;

/// <summary>
/// The structured-LLM <see cref="IWorkflowPlanner"/> (Rule 18.3 — an impl in the <c>Planners/</c> variant
/// folder). It resolves a structured-capable LLM client through the SAME <see cref="ILLMClientRegistry"/>
/// the <c>llm.complete</c> node uses, sends a system+user prompt constrained by
/// <see cref="PlannerSchema.ResponseSchema"/>, and deserializes the schema-valid object into a
/// <see cref="PlannedWorkflow"/>. Fails cleanly when no registered provider offers structured output.
///
/// <para>The planner produces DATA only — it never wires nodes or runs anything. The grounding context
/// (when present) is framed honestly as supplementary repo context, never as "I analyzed your codebase".</para>
/// </summary>
public sealed class LlmWorkflowPlanner : IWorkflowPlanner, IScopedDependency
{
    private readonly ILLMClientRegistry _clientRegistry;
    private readonly IModelPoolSelector _modelSelector;
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly Learning.ILessonReader _lessons;
    private readonly ILogger<LlmWorkflowPlanner>? _logger;

    /// <summary>Lessons shown per plan — the freshest few beat an exhaustive dump (prompt budget + recency bias are both deliberate). The SHARED window, so the supervisor lane's treatment is the same slice of the ledger.</summary>
    public const int LessonTopK = Learning.LessonArms.TopK;

    public LlmWorkflowPlanner(ILLMClientRegistry clientRegistry, IModelPoolSelector modelSelector, IAgentHarnessRegistry harnesses, Learning.ILessonReader lessons, ILogger<LlmWorkflowPlanner>? logger = null)
    {
        _clientRegistry = clientRegistry;
        _modelSelector = modelSelector;
        _harnesses = harnesses;
        _lessons = lessons;
        _logger = logger;
    }

    public async Task<PlannedWorkflow> PlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken)
    {
        // Resolve the brain: the operator's pinned model BY ROW ID when set (the same path the supervisor decider uses —
        // selected = verbatim, fail clearly if it isn't a structured-eligible team row); else auto-resolve a structured
        // client + a team pool model that MATCH — iterating the registered structured providers so a team whose pool is
        // ALL one provider (e.g. all Custom-gateway models) plans on THAT provider's client, not a provider-blind pick.
        var resolved = request.BrainModelId is { } brainModelId
            ? await InProcessStructuredModel.ResolveByRowIdAsync(_clientRegistry, _modelSelector, request.TeamId, brainModelId, cancellationToken).ConfigureAwait(false)
            : await InProcessStructuredModel.ResolveAsync(_clientRegistry, _modelSelector, new InProcessStructuredModelOptions(request.TeamId) { Logger = _logger }, cancellationToken).ConfigureAwait(false);

        if (resolved is not { } pickedBrain)
            throw new InvalidOperationException(request.BrainModelId is null
                ? "No structured-output LLM provider has a credentialed, enabled model in the team's pool. Add a model whose provider a registered structured client serves (Anthropic / OpenAI / a Custom OpenAI-compatible gateway)."
                : "The pinned brain model is not an enabled, structured-eligible model in the team's pool (missing / disabled / revoked / cross-team, or no registered structured client serves its provider).");

        var (structured, pick) = pickedBrain;

        // P2 — render the capability catalog (harnesses + drivable providers, the team's whole credentialed pool) so the
        // planner allocates a provider-compatible harness + model PER subtask informed, not blind. The run-time
        // reconciler is the backstop.
        var pool = await _modelSelector.ListPoolAsync(request.TeamId, allowedRowIds: null, cancellationToken).ConfigureAwait(false);
        var catalog = CapabilityCatalog.Render(_harnesses.All, pool);

        // D2 (cross-run learning): the distilled lessons ride the plan prompt — under a deterministic, toggle-free
        // A/B arm hashed from team + the UNDECORATED goal (never the prompt text, which a re-plan's feedback fold and
        // the flat-plan constraint both move), so the same task lands in the same arm here and on the supervisor lane.
        var current = await _lessons.ListCurrentAsync(request.TeamId, request.RepositoryId, LessonTopK, cancellationToken).ConfigureAwait(false);
        var arm = Learning.LessonArms.For(request.TeamId, request.TaskGoal ?? request.TaskText, current.Count);
        var injected = arm == Learning.LessonArms.Injected ? current : Array.Empty<Persistence.Entities.Lesson>();

        var completion = await structured.CompleteStructuredAsync(BuildRequest(request, pick, catalog, injected), cancellationToken).ConfigureAwait(false);

        // Stamped from the model that actually ANSWERED (a pool failover may have hopped past the resolved pick), so
        // the plan carries its own provenance.
        return Deserialize(completion.Json, request.DeclaredDeliverablePaths) with
        {
            AuthoredByModel = completion.Model,
            AuthoredByObservedModel = completion.ObservedModel,
            LessonArm = arm,
            InjectedLessonIds = injected.Count > 0 ? injected.Select(l => l.Id).ToList() : null,
        };
    }

    internal static StructuredLLMCompletionRequest BuildRequest(WorkflowPlanRequest request, ModelPoolPick pick, string catalog, IReadOnlyList<Persistence.Entities.Lesson> lessons) => new()
    {
        Model = pick.ModelId,
        Credential = pick.Credential,
        SystemPrompt = SystemPrompt,
        UserPrompt = BuildUserPrompt(request, catalog, lessons),
        JsonSchema = PlannerSchema.ResponseSchema,
        ResponseValidator = ValidateModelResponse,
        ResponseAdvisor = AdviseModelResponse,
        MaxOutputTokens = 4096,
        Temperature = 0.2,
    };

    /// <summary>The typed runtime boundary also participates in the provider's existing bounded re-ask. What is left for it to FAIL on is the plan's own shape — no subtask at all, or a <c>subtasks</c> that binds to no <c>PlannedWorkflow</c>: there is no subtask left to degrade. An acceptance-level defect never reaches here; <see cref="AdviseModelResponse"/> reports those at the severity they cost.</summary>
    internal static IReadOnlyList<string> ValidateModelResponse(JsonElement response)
    {
        try
        {
            var plan = PlannerAcceptanceDraft.ReadResponse(response, out _);
            return plan is null || plan.Subtasks.Count == 0 ? ["The planner response requires at least one subtask."] : [];
        }
        catch (JsonException ex)
        {
            return [$"Planner response contract: {ex.Message}"];
        }
    }

    /// <summary>
    /// The acceptance defects the plan outlives — EVERY one the typed contract cannot bind, not just an absent
    /// payload. They ride the SAME bounded re-ask, with the offending subtask and the contract's own words about it,
    /// but a reply that still does not bind costs that subtask its oracle, not the run its plan. Naming the subtask
    /// and the concrete defects matters twice over: "somewhere in your plan" is not a correction a model can act on,
    /// and there is only ONE re-ask, so advice that names half the problem buys a second reply with the other half
    /// still wrong (live run 34093741284: every acceptance missing both <c>formatVersion</c> and its payload).
    ///
    /// <para>Each finding also CLAIMS its acceptance's own instance path, so the SCHEMA violations the same defect
    /// raises — the per-kind <c>oneOf</c> branch it cannot match, the payload's <c>minItems</c>, the missing
    /// <c>formatVersion</c>, the <c>kind</c> enum — are read as this one degradable defect rather than as a fault.
    /// The claim is per POSITION and only for an acceptance this contract itself refuses to bind, so a defect in a
    /// sibling subtask, or anywhere in the plan OUTSIDE an acceptance, is untouched and still fatal.</para>
    /// </summary>
    internal static IReadOnlyList<StructuredResponseAdvisory> AdviseModelResponse(JsonElement response) =>
        PlannerAcceptanceDraft.DescribeUnboundAcceptances(response).Select(unbound => new StructuredResponseAdvisory
        {
            Path = $"$.subtasks[{unbound.Index}].acceptance",
            Message = $"Subtask '{unbound.Drop.SubtaskId}' authored an acceptance this contract cannot bind, so that subtask will be graded by nothing. {unbound.Drop.Reason} Re-author the whole acceptance object correctly, or omit it entirely.",
        }).ToArray();

    /// <summary>Internal test accessor (InternalsVisibleTo) — pins the prompt framing + over-claim guard directly, without a real LLM round-trip.</summary>
    internal static string BuildUserPromptForTest(WorkflowPlanRequest request, string catalog = "", IReadOnlyList<Persistence.Entities.Lesson>? lessons = null) => BuildUserPrompt(request, catalog, lessons ?? Array.Empty<Persistence.Entities.Lesson>());

    private static string BuildUserPrompt(WorkflowPlanRequest request, string catalog, IReadOnlyList<Persistence.Entities.Lesson> lessons)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Task to plan:");
        builder.AppendLine(request.TaskText);

        if (!string.IsNullOrWhiteSpace(request.GroundingContext))
        {
            builder.AppendLine();
            builder.AppendLine("Repository top-level layout (use it to ground the plan). This is a top-level listing only — it is NOT a full code analysis, so do not assume anything below the named entries:");
            builder.AppendLine(request.GroundingContext);
        }

        if (!string.IsNullOrWhiteSpace(catalog))
        {
            builder.AppendLine();
            builder.AppendLine(catalog.TrimEnd());
        }

        if (lessons.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Lessons distilled from this team's prior failed runs (real post-mortems — apply them where they fit the task):");
            foreach (var lesson in lessons)
                builder.AppendLine($"- {Learning.LessonArms.Line(lesson)}");
        }

        // IMPROVE: an independent reviewer critiqued a prior draft of this plan — revise to address it (set by the
        // CriticPlannerDecorator on its one re-plan; absent on a first pass).
        if (!string.IsNullOrWhiteSpace(request.ReviewerCritique))
        {
            builder.AppendLine();
            builder.AppendLine("An independent reviewer critiqued a PRIOR draft of this plan. Produce an improved plan that addresses this critique:");
            builder.AppendLine(request.ReviewerCritique);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Internal (not private) so the DTO-bind boundary is unit-pinned directly — the failure it converts escaped as
    /// an unhandled crash for as long as nothing tested it. <paramref name="declaredDeliverablePaths"/> defaults to
    /// <c>null</c> — no declaration source at all — so every existing caller that does not carry an
    /// operator-declared deliverable list still admits a planner-authored <c>ArtifactPresent</c> PAIRED with an
    /// ArtifactSchema/LlmJudge content check (promoted to that companion's kind); a bare (unpaired) one is always
    /// self-certifying and always dropped regardless
    /// (see <see cref="PlannerAcceptanceDraft.ReconcileArtifactPresent(PlannedWorkflow, IReadOnlyCollection{string}?, out IReadOnlyList{DroppedAcceptance})"/>).
    /// </summary>
    internal static PlannedWorkflow Deserialize(JsonElement json, IReadOnlyCollection<string>? declaredDeliverablePaths = null)
    {
        PlannedWorkflow? plan;
        IReadOnlyList<DroppedAcceptance> dropped;

        try
        {
            plan = PlannerAcceptanceDraft.ReadResponse(json, out dropped);
        }
        catch (JsonException ex)
        {
            // A reply that is well-formed JSON but does not BIND to the DTO — e.g. `subtasks` as an array of bare
            // strings where the schema declares objects. The structured-output repair path handles malformed JSON;
            // it cannot see this one, because nothing is malformed. Left raw it escaped as "Node planner threw
            // unhandled exception", which reads as an engine crash rather than a contract mismatch and tells an
            // operator nothing about which field disagreed.
            throw new InvalidOperationException($"The planner's reply did not match the planner schema's shape: {ex.Message}", ex);
        }

        if (plan == null || plan.Subtasks.Count == 0)
            throw new InvalidOperationException("The planner returned an empty plan (no subtasks). The response did not conform to the planner schema.");

        // P2.6: a well-bound ArtifactPresent can still be self-certifying — reconciled AFTER the bind above (which
        // only ever costs a subtask its oracle, never the plan) so this drop rides the exact same policy.
        plan = PlannerAcceptanceDraft.ReconcileArtifactPresent(plan, declaredDeliverablePaths, out var selfCertifying);

        var allDropped = selfCertifying.Count == 0 ? dropped : dropped.Concat(selfCertifying).ToList();

        // An acceptance the re-ask could not get authored is carried as a NAMED defect on the plan, not thrown away
        // silently and not thrown at all: the subtask keeps its work with no oracle (graded unverified downstream).
        // Stamped UNCONDITIONALLY, exactly like the AuthoredByModel / LessonArm / InjectedLessonIds siblings above: a
        // conditional stamp leaves a model-authored value standing on a clean plan, and this field is a DEFECT REPORT
        // — the one thing a model must never be able to write about its own reply.
        return plan with { DroppedAcceptances = allDropped.Count == 0 ? null : allDropped };
    }

    // Internal (not private): the planner-cassette drift detector reconstructs the EXACT run-time request from
    // these production seams, so a prompt edit moves the pinned cassette key loudly (Rule 12.5).
    internal const string SystemPrompt =
        "You are a senior engineer turning a free-text task into a concrete, reviewable plan. " +
        "A task's DELIVERABLE may be an answer, a written document, a code change or read-only research findings — " +
        "identify the requested result and the evidence that would establish completion. " +
        "Break the task into a small number of ordered, independently-executable subtasks (1–20). " +
        "Give each subtask a stable id, a short title, and a concrete instruction. " +
        "State the success criteria a reviewer would check and the main risks. " +
        "Set recommendedWorkflowKind to 'coding' when the subtasks are code changes a coding agent should make, " +
        "otherwise 'analysis'. " +
        "When a capability catalog is provided, you MAY give each subtask its best-fit harness + model from it — pick a " +
        "model from the listed pool and a harness whose providers can drive that model's provider; omit them to use the " +
        "run defaults. " +
        "When a subtask's completion can be objectively verified, author acceptance with formatVersion 2 and an explicit oracle kind. " +
        "TestsPass uses argv: the exact executable and argument tokens, including any intentional empty arguments. " +
        "ArtifactPresent, LlmJudge, CitationsResolve and ArtifactSchema use artifactPaths: workspace-relative files the selected oracle reads. " +
        "Choose the oracle for the required evidence independently of the subtask's kind; neither coding nor research determines the oracle. " +
        "Supply exactly one of argv or artifactPaths. A file-presence requirement names files in artifactPaths; executable checks belong in argv. " +
        "LlmJudge also requires its rubric, and ArtifactSchema its schema. Do not substitute existence for required content or behavioral verification. " +
        "ArtifactPresent is kept only for an operator-declared deliverable path AND when you also give it a rubric or schema of its own; otherwise it is dropped, so prefer TestsPass, or ArtifactSchema/LlmJudge directly, to grade a subtask's own output. " +
        "A proposal is not evidence that a command ran or that an execution workspace or dependency is available. " +
        "Omit acceptance when no objective check exists. Add short subjective acceptanceCriteria a reviewer checks when they " +
        "add real signal. Use dependsOn to order subtasks that need another subtask's result; you MAY type each " +
        "subtask with a short open kind (research / code / analysis / write). " +
        "When the goal leaves a REAL direction choice open, author up to 3 questions, each with 2-4 mutually " +
        "exclusive options and a recommendedOptionId; record the defaults you proceed on under assumptions. Omit " +
        "questions when the plan needs no operator input. " +
        "Set hasEnoughContext true ONLY when the goal needs no execution at all; even at 90% certainty, prefer false. " +
        "Even with hasEnoughContext true you MUST still author at least one subtask — make it the single step that " +
        "states the answer/summary. " +
        "Return ONLY the schema-constrained JSON.";
}
