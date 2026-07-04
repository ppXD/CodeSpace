namespace CodeSpace.Messages.Dtos.Sessions.Journal;

/// <summary>
/// One planned subtask on a supervisor PLAN step — the plan the model authored, rendered inline right under "planned the
/// work" so the causal spine reads plan → dispatch → agents. <see cref="SubtaskId"/> is the plan-local id (the join key a
/// deferred / agent card references); <see cref="Title"/> is the short display line. A re-plan is a later Plan step
/// carrying its OWN subtasks, so the chronological journal shows each planning moment where it happened — no grouping.
/// </summary>
public sealed record JournalSubtask
{
    /// <summary>The plan-local id of the subtask.</summary>
    public required string SubtaskId { get; init; }

    /// <summary>The subtask's short title — its display line in the inline plan.</summary>
    public required string Title { get; init; }
}
