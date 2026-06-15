namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The OPEN-STRING launch surfaces a task can come from (Rule 18.1, a pure data noun) — the wire value a
/// <c>LaunchTaskCommand.SurfaceKind</c> / <c>TaskLaunchRequest.SurfaceKind</c> carries and the
/// <c>ITaskLaunchSeedProviderRegistry</c> resolves a seed provider by. Consts (NOT an enum, Rule 18.1) so a new
/// surface is a new const + a new seed-provider folder, never a core-enum edit. <see cref="Chat"/> is the only
/// surface with a provider in this PR; the rest are RESERVED names a later PR adds providers for (the real
/// pr/issue/project/repo seed derivation from the source entity) — they exist now only so a caller can refer to
/// them by a stable string, and the genericity contract proves any provider plugs in with zero core edit.
/// </summary>
public static class TaskLaunchSurfaceKinds
{
    /// <summary>A free-text chat task — the goal IS the task text; the only surface with a seed provider in this PR.</summary>
    public const string Chat = "chat";

    /// <summary>RESERVED: a task launched from a pull request. No seed provider yet.</summary>
    public const string Pr = "pr";

    /// <summary>RESERVED: a task launched from an issue. No seed provider yet.</summary>
    public const string Issue = "issue";

    /// <summary>RESERVED: a task launched from a project. No seed provider yet.</summary>
    public const string Project = "project";

    /// <summary>RESERVED: a task launched against a whole repository. No seed provider yet.</summary>
    public const string Repo = "repo";
}
