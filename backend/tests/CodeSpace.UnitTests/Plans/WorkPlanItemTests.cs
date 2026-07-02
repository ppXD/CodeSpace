using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Plans;
using Shouldly;

namespace CodeSpace.UnitTests.Plans;

/// <summary>
/// The unified plan-item vocabulary (triad S1): BOTH producers — the plan.author node's
/// <see cref="PlannedSubtask"/> and the supervisor's <see cref="SupervisorPlannedSubtask"/> — map into ONE
/// <see cref="WorkPlanItem"/> shape, and a minimal item's persisted bytes stay stable (null-omitted optionals)
/// as the contract grows.
/// </summary>
[Trait("Category", "Unit")]
public class WorkPlanItemTests
{
    [Fact]
    public void Maps_the_full_contract_from_a_planned_subtask()
    {
        var item = WorkPlanItem.From(new PlannedSubtask
        {
            Id = "s2",
            Title = "Second",
            Instruction = "do the second thing",
            Rationale = "depends on the first",
            Kind = "research",
            DependsOn = new[] { "s1" },
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" }, Kind = BenchmarkGradingKind.ArtifactPresent, Description = "the check" },
            AcceptanceCriteria = new[] { "cites sources" },
            Harness = "claude-code",
            Model = "some-model",
        });

        item.Id.ShouldBe("s2");
        item.Title.ShouldBe("Second");
        item.Instruction.ShouldBe("do the second thing");
        item.Rationale.ShouldBe("depends on the first");
        item.Kind.ShouldBe("research");
        item.DependsOn.ShouldBe(new[] { "s1" });
        item.Acceptance!.Command.ShouldBe(new[] { "sh", "check.sh" });
        item.Acceptance.Kind.ShouldBe(BenchmarkGradingKind.ArtifactPresent);
        item.AcceptanceCriteria.ShouldBe(new[] { "cites sources" });
        item.Harness.ShouldBe("claude-code");
        item.Model.ShouldBe("some-model");
    }

    [Fact]
    public void Maps_the_contract_from_a_supervisor_planned_subtask()
    {
        var item = WorkPlanItem.From(new SupervisorPlannedSubtask
        {
            Id = "sa",
            Title = "Alpha",
            Instruction = "do alpha",
            DependsOn = new[] { "sb" },
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "dotnet", "test" } },
        });

        item.Id.ShouldBe("sa");
        item.Title.ShouldBe("Alpha");
        item.Instruction.ShouldBe("do alpha");
        item.DependsOn.ShouldBe(new[] { "sb" });
        item.Acceptance!.Command.ShouldBe(new[] { "dotnet", "test" });
        item.Rationale.ShouldBeNull("the supervisor vocabulary has no rationale — absent, never invented");
        item.Harness.ShouldBeNull();
        item.Model.ShouldBeNull();
    }

    [Fact]
    public void A_minimal_item_serializes_to_exactly_three_keys()
    {
        // The persisted items_json contract: optionals are OMITTED, not null-valued — so a minimal item's
        // bytes are stable as the contract grows, and readers can distinguish "uncontracted" from "null".
        var json = JsonSerializer.Serialize(new WorkPlanItem { Id = "a", Title = "T", Instruction = "I" }, AgentJson.Options);

        json.ShouldBe("""{"id":"a","title":"T","instruction":"I"}""");
    }

    [Fact]
    public void The_acceptance_kind_serializes_as_its_name()
    {
        // The enum rides AgentJson's string-enum converter — the SAME vocabulary the supervisor tape uses, so
        // per-item acceptance is greppable/projectable without an int decoder table.
        var json = JsonSerializer.Serialize(WorkPlanItem.From(new SupervisorPlannedSubtask
        {
            Id = "a",
            Title = "T",
            Instruction = "I",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "x" }, Kind = BenchmarkGradingKind.ArtifactPresent },
        }), AgentJson.Options);

        json.ShouldContain("\"kind\":\"ArtifactPresent\"");
    }
}
