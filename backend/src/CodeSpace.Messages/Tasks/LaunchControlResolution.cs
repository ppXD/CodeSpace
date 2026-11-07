using CodeSpace.Messages.Agents;

namespace CodeSpace.Messages.Tasks;

/// <summary>The dispositions of one launch's route-dependent controls, the route + workspace's operator-command acceptance verdict they were derived from, and the pooled row the single agent runs on when its pinned model had to move inside the allowed pool (null when nothing was clamped).</summary>
public sealed record LaunchControlResolution(IReadOnlyList<LaunchControlDisposition> Dispositions, TaskAcceptanceCompatibility AcceptanceCompatibility, PooledModelRow? ModelClamp);
