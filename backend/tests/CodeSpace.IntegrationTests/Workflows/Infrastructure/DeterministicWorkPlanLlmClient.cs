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

    /// <summary>The goal the fake restates — asserted verbatim by the flow tests.</summary>
    public const string PlannedGoal = "Ship the planned goal";

    private readonly WorkPlanPlanScript _script;

    public DeterministicWorkPlanLlmClient(WorkPlanPlanScript script) { _script = script; }

    public string Provider => ProviderTag;

    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LLMCompletion { Text = PlannedGoal, Model = request.Model, Usage = new() { InputTokens = 3, OutputTokens = 5 } });

    public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var subtasks = _script.AuthorContract
            ? new object[]
            {
                new { id = "s1", title = "First", instruction = "do the first thing" },
                new { id = "s2", title = "Second", instruction = "do the second thing", dependsOn = new[] { "s1" }, acceptance = new { command = AcceptanceCommand, kind = "TestsPass", description = "the unit check" } },
            }
            : new object[]
            {
                new { id = "s1", title = "First", instruction = "do the first thing" },
                new { id = "s2", title = "Second", instruction = "do the second thing" },
            };

        var json = JsonSerializer.SerializeToElement(new
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

    public void Reset()
    {
        AuthorContract = false;
        HasEnoughContext = false;
    }
}
