using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Messages.Agents;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Learning;

[Trait("Category", "Unit")]
public sealed class AgentLessonPromptTests
{
    [Fact]
    public void Injection_uses_the_system_channel_and_freezes_exact_receipts_without_rewriting_the_goal()
    {
        var lesson = new Lesson { Id = Guid.NewGuid(), FailureClass = "build", WhatFailed = "restore failed", HowToApply = "run restore first" };
        var task = new AgentTask { Goal = "Fix the project", Harness = "claude-code", SystemPrompt = "persona" };

        var result = AgentLessonPrompt.Inject(task, LessonArms.Injected, [lesson]);

        result.Goal.ShouldBe(task.Goal);
        result.SystemPrompt.ShouldStartWith("persona");
        result.SystemPrompt.ShouldContain(LessonArms.Line(lesson));
        result.LessonArm.ShouldBe(LessonArms.Injected);
        result.LessonIds.ShouldBe([lesson.Id]);
    }

    [Theory]
    [InlineData(LessonArms.Withheld)]
    [InlineData(LessonArms.None)]
    public void Non_treatment_arms_freeze_assignment_without_adding_prompt_text(string arm)
    {
        var task = new AgentTask { Goal = "Fix", Harness = "claude-code", SystemPrompt = "persona" };

        var result = AgentLessonPrompt.Inject(task, arm, []);

        result.SystemPrompt.ShouldBe("persona");
        result.LessonArm.ShouldBe(arm);
        result.LessonIds.ShouldBeEmpty();
    }

    [Fact]
    public void Legacy_tasks_omit_learning_fields_while_frozen_receipts_round_trip()
    {
        var legacy = new AgentTask { Goal = "Fix", Harness = "claude-code" };
        JsonSerializer.Serialize(legacy, CodeSpace.Core.Services.Agents.AgentJson.Options).ShouldNotContain("lessonArm");

        var frozen = AgentLessonPrompt.Inject(legacy, LessonArms.Withheld, []);
        var roundTrip = JsonSerializer.Deserialize<AgentTask>(JsonSerializer.Serialize(frozen, CodeSpace.Core.Services.Agents.AgentJson.Options), CodeSpace.Core.Services.Agents.AgentJson.Options)!;

        roundTrip.LessonArm.ShouldBe(LessonArms.Withheld);
        roundTrip.LessonIds.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(LessonArms.Injected, true)]
    [InlineData(LessonArms.Withheld, true)]
    [InlineData(LessonArms.None, true)]
    [InlineData("future-or-corrupt", false)]
    [InlineData(null, false)]
    public void Only_server_owned_arm_values_are_accepted(string? arm, bool expected) => LessonArms.IsKnown(arm).ShouldBe(expected);

    [Fact]
    public void Reapplying_the_same_receipt_is_prompt_idempotent()
    {
        var lesson = new Lesson { Id = Guid.NewGuid(), FailureClass = "test", WhatFailed = "a check failed", HowToApply = "run the exact check" };
        var once = AgentLessonPrompt.Inject(new AgentTask { Goal = "Fix", Harness = "claude-code" }, LessonArms.Injected, [lesson]);

        var twice = AgentLessonPrompt.Inject(once, LessonArms.Injected, [lesson]);

        twice.SystemPrompt.ShouldBe(once.SystemPrompt);
        Should.Throw<ArgumentOutOfRangeException>(() => AgentLessonPrompt.Inject(once, "corrupt", [lesson]));
    }
}
