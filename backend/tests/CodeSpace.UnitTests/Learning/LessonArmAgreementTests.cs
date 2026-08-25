using System.Text.Json;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Projection.Builders.Supervisor;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Learning;

/// <summary>
/// 🟢 Unit: the D2 lesson A/B's ASSIGNMENT INPUT, pinned across BOTH brain lanes.
///
/// <para>The arm is a SHA-256 over (team, assignment text), so any byte difference in what a lane feeds it
/// re-rolls the arm with ~50% probability. Both lanes therefore have to hash the SAME text for the same task —
/// the OPERATOR'S GOAL, before any lane-local decoration (the planner's acceptance-criteria fold, its
/// operator-feedback fold and its flat-plan constraint; the supervisor projection's prepended session grounding).
/// Each case is driven through the REAL composers — <see cref="PlanAuthorNode.BuildPlanRequest"/> on one side, the
/// real <see cref="SupervisorDefinitionBuilder"/> projection plus the node's own config reader on the other — so a
/// new decoration on either side, or a dropped <c>displayTitle</c> in the projection, surfaces here as a
/// disagreement rather than as silent arm churn.</para>
/// </summary>
[Trait("Category", "Unit")]
public class LessonArmAgreementTests
{
    /// <summary>Fixed (not <c>Guid.NewGuid()</c>) so the hash — and therefore any failure — is reproducible.</summary>
    private static readonly Guid TeamId = Guid.Parse("6f2f5d0e-8a91-4f52-9a4d-2f6b1c0d7e33");

    public static TheoryData<string, string, string, bool> Cases => new()
    {
        // goal, grounding (the session thread-context a CONTINUE prepends), feedback (the edit-loop re-plan), flatPlan
        { "Fix the failing auth tests and open a PR", "", "", false },
        { "Fix the failing auth tests and open a PR", "Prior turn: added the login form.", "", false },
        { "Migrate the billing tables", "", "Split the migration in two.", false },
        { "Migrate the billing tables", "Prior turn: drafted the schema.", "Split the migration in two.", true },
        { "Add rate limiting to the public API", "", "", true },
        { "Add rate limiting to the public API", "Prior turn: benchmarked the gateway.", "", true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Both_brain_lanes_assign_the_same_arm_to_the_same_task(string goal, string grounding, string feedback, bool flatPlan)
    {
        var criteria = new[] { "tests pass", "PR opened" };

        var planner = LessonArms.Assign(TeamId, PlannerAssignmentText(goal, criteria, grounding, feedback, flatPlan));
        var supervisor = LessonArms.Assign(TeamId, SupervisorAssignmentText(goal, grounding));

        planner.ShouldBe(supervisor,
            customMessage: $"the planner and supervisor lanes must inject-or-withhold the SAME task identically — otherwise the A/B's control group is contaminated by whichever lane ran. goal='{goal}' grounding={grounding.Length}ch feedback={feedback.Length}ch flatPlan={flatPlan}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Neither_lane_hashes_its_own_prompt_decorations(string goal, string grounding, string feedback, bool flatPlan)
    {
        // The undecorated goal is what identifies the task. Its decorated forms — which is what BOTH lanes hashed
        // before this was fixed — must not leak into the assignment text, or a re-plan and a continue land elsewhere.
        PlannerAssignmentText(goal, ["tests pass"], grounding, feedback, flatPlan).ShouldBe(goal);
        SupervisorAssignmentText(goal, grounding).ShouldBe(goal);
    }

    /// <summary>What the PLANNER lane feeds the assignment, off the real <see cref="PlanAuthorNode.BuildPlanRequest"/> the plan.author / plan.confirm nodes call.</summary>
    private static string PlannerAssignmentText(string goal, IReadOnlyList<string> criteria, string grounding, string feedback, bool flatPlan) =>
        PlanAuthorNode.BuildPlanRequest(PlannerConfig(flatPlan), TeamId, new PlanAuthorNode.PlanPromptParts(goal, criteria, grounding, feedback)).TaskGoal!;

    /// <summary>What the SUPERVISOR lane feeds the assignment: the REAL projection's node config, read back exactly as the node reads it, then through rehydrate's own assignment-goal reader.</summary>
    private static string SupervisorAssignmentText(string goal, string grounding)
    {
        var config = SupervisorNodeConfig(goal, grounding);

        return SupervisorTurnService.LessonAssignmentGoal(config["goal"].GetString()!, AgentSupervisorNode.ReadGoalConfig(config)!);
    }

    private static Dictionary<string, JsonElement> SupervisorNodeConfig(string goal, string grounding)
    {
        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = goal, SurfaceKind = "chat", TeamId = TeamId },
            Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.Supervisor, Caps = new RouteCaps() },
            GroundingContext = string.IsNullOrEmpty(grounding) ? null : grounding,
        };

        return new SupervisorDefinitionBuilder().Build(context).Nodes.Single(n => n.Id == "sup")
            .Config.Deserialize<Dictionary<string, JsonElement>>()!;
    }

    private static IReadOnlyDictionary<string, JsonElement> PlannerConfig(bool flatPlan) =>
        new Dictionary<string, JsonElement> { ["flatPlan"] = JsonSerializer.SerializeToElement(flatPlan) };
}
