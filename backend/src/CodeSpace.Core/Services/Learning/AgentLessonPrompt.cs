using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Learning;

/// <summary>Pure projection of a frozen lesson assignment onto an agent task's native system-prompt channel.</summary>
public static class AgentLessonPrompt
{
    public static AgentTask Inject(AgentTask task, string arm, IReadOnlyList<Lesson> lessons)
    {
        if (!LessonArms.IsKnown(arm)) throw new ArgumentOutOfRangeException(nameof(arm), arm, "Unknown lesson assignment arm.");

        var injected = arm == LessonArms.Injected ? lessons : [];
        var section = injected.Count == 0 ? null : "Cross-run lessons from prior task outcomes:\n" + string.Join("\n", injected.Select(lesson => "- " + LessonArms.Line(lesson)));
        var alreadyInjected = section is not null && task.SystemPrompt?.Contains(section, StringComparison.Ordinal) == true;
        var systemPrompt = section is null || alreadyInjected ? task.SystemPrompt : string.IsNullOrWhiteSpace(task.SystemPrompt) ? section : task.SystemPrompt.TrimEnd() + "\n\n" + section;

        return task with { SystemPrompt = systemPrompt, LessonArm = arm, LessonIds = injected.Select(lesson => lesson.Id).ToList() };
    }
}
