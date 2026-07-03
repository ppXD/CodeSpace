using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The HONEST structured fake for the <c>plan.author</c> node flows — the CONTRACT-authoring sibling of
/// <see cref="DeterministicTaskPlannerLlmClient"/>. The node runs the production <c>LlmWorkflowPlanner</c>
/// (real registry resolve, real <c>CompleteStructuredAsync</c>, real <c>PlannerSchema.Options</c>
/// deserialization); only the network call is replaced. Unlike the task-planner fake, this one can author
/// the FULL plan contract — per-subtask <c>dependsOn</c> + <c>acceptance</c> and the plan-level
/// <c>hasEnoughContext</c> self-bypass — knob-driven via the fixture-singleton <see cref="WorkPlanPlanScript"/>
/// (a test mutates it, and MUST reset it in a finally/Dispose — the fixture is shared).
/// </summary>
public sealed class DeterministicWorkPlanLlmClient : ILLMClient, IStructuredLLMClient
{
    /// <summary>Distinct provider tag — sits beside the other fakes + the real client with no duplicate-provider collision.</summary>
    public const string ProviderTag = "TestWorkPlanner";

    /// <summary>The check argv the contract-authoring plan puts on its second subtask's acceptance.</summary>
    public static readonly IReadOnlyList<string> AcceptanceCommand = new[] { "sh", "check.sh" };

    /// <summary>The rubric-contract plan's single deliverable path — the fixed file <see cref="ReviseHealingFakeCli"/> writes.</summary>
    public const string RubricDeliverablePath = ReviseHealingFakeCli.FileName;

    /// <summary>The rubric-contract plan's single criterion id — met IFF the deliverable carries <c>MEETS[healed]</c> (the revise-round content).</summary>
    public const string RubricCriterionId = "healed";

    /// <summary>The goal the fake restates — asserted verbatim by the flow tests.</summary>
    public const string PlannedGoal = "Ship the planned goal";

    private readonly WorkPlanPlanScript _script;

    public DeterministicWorkPlanLlmClient(WorkPlanPlanScript script) { _script = script; }

    /// <summary>The default plan's instructions — the map projections bind each branch goal to <c>{{item.instruction}}</c>, so the fan-out E2Es pin agents against these verbatim.</summary>
    public static readonly IReadOnlyList<string> DefaultInstructions = new[] { "do the first thing", "do the second thing" };

    /// <summary>The REVISED plan's single merged instruction — returned whenever the request carries operator feedback (the plan.confirm edit loop), so the confirm E2Es can prove v2 ≠ v1 propagated to the fan-out.</summary>
    public const string RevisedInstruction = "do both things in one pass";

    /// <summary>The heterogeneous plan (<c>AuthorHeterogeneousKinds</c>) — one research + two code items, the dynamic fan-out's mode-mapping fodder (kind binds <c>{{item.kind}}</c> → the node's permission/push mapping).</summary>
    public static readonly IReadOnlyList<(string Instruction, string Kind)> HeterogeneousItems = new[]
    {
        ("Work on alpha", "research"),
        ("Work on beta", "code"),
        ("Work on gamma", "code"),
    };

    public string Provider => ProviderTag;

    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LLMCompletion { Text = PlannedGoal, Model = request.Model, Usage = new() { InputTokens = 3, OutputTokens = 5 } });

    public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        object[] subtasks;
        if (request.UserPrompt.Contains("feedback", StringComparison.OrdinalIgnoreCase) && !_script.AuthorInvalidDag && !_script.AuthorHeterogeneousKinds && !_script.AuthorContract && !_script.AuthorRubricContract)
            subtasks = new object[] { new { id = "r1", title = "Merged", instruction = RevisedInstruction } };
        else if (_script.AuthorRubricContract)
            subtasks = new object[]
            {
                new
                {
                    id = "n1", title = "Write the report", instruction = "write the research report", kind = "research",
                    acceptance = new
                    {
                        command = new[] { RubricDeliverablePath },
                        kind = "LlmJudge",
                        description = "the report satisfies the rubric",
                        rubric = new
                        {
                            criteria = new object[] { new { id = RubricCriterionId, requirement = "the report carries the revised content" } },
                            judgeModelId = _script.RubricJudgeModelId,
                        },
                    },
                },
            };
        else if (_script.AuthorInvalidDag)
            subtasks = new object[] { new { id = "s1", title = "First", instruction = "do the first thing", dependsOn = new[] { "ghost" } } };
        else if (_script.AuthorHeterogeneousKinds)
            subtasks = HeterogeneousItems.Select((item, i) => (object)new { id = $"h{i + 1}", title = item.Instruction, instruction = item.Instruction, kind = item.Kind }).ToArray();
        else if (_script.AuthorContract)
            subtasks = new object[]
            {
                new { id = "s1", title = "First", instruction = "do the first thing", kind = "research" },
                new { id = "s2", title = "Second", instruction = "do the second thing", dependsOn = new[] { "s1" }, acceptance = new { command = AcceptanceCommand, kind = "TestsPass", description = "the unit check" }, acceptanceCriteria = new[] { "covers edge cases" } },
            };
        else
            subtasks = new object[]
            {
                new { id = "s1", title = "First", instruction = "do the first thing" },
                new { id = "s2", title = "Second", instruction = "do the second thing" },
            };

        var json = _script.AuthorContract
            ? JsonSerializer.SerializeToElement(new
            {
                goal = PlannedGoal,
                subtasks,
                successCriteria = new[] { "both things done" },
                risks = new[] { "unknowns" },
                recommendedWorkflowKind = "analysis",
                hasEnoughContext = _script.HasEnoughContext,
                assumptions = new[] { "assumed the default branch" },
                questions = new object[]
                {
                    new { id = "q1", question = "Which direction?", options = new object[] { new { id = "a", label = "Fast" }, new { id = "b", label = "Thorough" } }, recommendedOptionId = "a", allowFreeText = false },
                },
            })
            : JsonSerializer.SerializeToElement(new
            {
                goal = PlannedGoal,
                subtasks,
                successCriteria = new[] { "both things done" },
                risks = new[] { "unknowns" },
                recommendedWorkflowKind = "analysis",
                hasEnoughContext = _script.HasEnoughContext,
            });

        return Task.FromResult(new StructuredLLMCompletion { Json = json, Model = request.Model, Usage = new() { InputTokens = 13, OutputTokens = 17 } });
    }
}

/// <summary>The fixture-singleton knob the work-plan fake reads. Mutate per test; ALWAYS <see cref="Reset"/> in cleanup (the fixture is shared across the Postgres collection).</summary>
public sealed class WorkPlanPlanScript
{
    /// <summary>When true, subtask s2 authors dependsOn + an objective acceptance (the full contract).</summary>
    public bool AuthorContract { get; set; }

    /// <summary>The plan-level self-bypass the node surfaces as <c>executionNeeded = false</c>.</summary>
    public bool HasEnoughContext { get; set; }

    /// <summary>When true, the plan authors a DANGLING dependsOn — the structurally-invalid DAG the node must fail closed on.</summary>
    public bool AuthorInvalidDag { get; set; }

    /// <summary>When true, the plan is the HETEROGENEOUS three-item shape (<c>DeterministicWorkPlanLlmClient.HeterogeneousItems</c>) — one research + two code kinds, for the dynamic fan-out's mode mapping.</summary>
    public bool AuthorHeterogeneousKinds { get; set; }

    /// <summary>When true, the plan is ONE research item carrying an <c>LlmJudge</c> acceptance (triad S7): deliverable <c>DeterministicWorkPlanLlmClient.RubricDeliverablePath</c>, one criterion <c>RubricCriterionId</c>, judge pinned to <see cref="RubricJudgeModelId"/>.</summary>
    public bool AuthorRubricContract { get; set; }

    /// <summary>The judge pool row the rubric-contract plan pins (the test seeds the row, then sets this — the fake can't know the guid).</summary>
    public Guid? RubricJudgeModelId { get; set; }

    public void Reset()
    {
        AuthorContract = false;
        HasEnoughContext = false;
        AuthorInvalidDag = false;
        AuthorHeterogeneousKinds = false;
        AuthorRubricContract = false;
        RubricJudgeModelId = null;
    }
}
