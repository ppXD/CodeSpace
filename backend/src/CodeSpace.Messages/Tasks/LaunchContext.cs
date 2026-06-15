using System.Text.Json;

namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The opaque per-surface launch payload (Rule 18.1, a pure data noun) — the surface that issues a launch attaches
/// whatever raw context it owns (a PR number, an issue iid, a chat conversation id, …) as a free-form
/// <see cref="Raw"/> JSON element under a flat <see cref="SurfaceKind"/> string.
///
/// <para><b>The no-hardcode keystone.</b> The launch CORE NEVER deserializes <see cref="Raw"/>. It cannot branch on
/// surface semantics because it never reads the payload — only the per-surface <c>ITaskLaunchSeedProvider</c>
/// resolved by <see cref="SurfaceKind"/> reads it (the handler folds <see cref="Raw"/> into
/// <c>TaskLaunchRequest.SurfacePayload</c>, which only that provider interprets). So a NEW surface adds a provider
/// + a new <c>SurfaceKind</c> string and ships, with zero edit to the core launch service / registry — proven by
/// the genericity contract test (a fake provider derives its goal entirely from the surface payload).</para>
/// </summary>
public sealed record LaunchContext
{
    /// <summary>The launch surface this context came from (an open <see cref="TaskLaunchSurfaceKinds"/> string) — the registry resolves a seed provider by it.</summary>
    public required string SurfaceKind { get; init; }

    /// <summary>The opaque per-surface payload. The core never reads it — only the resolved seed provider does. Defaults to an empty object.</summary>
    public JsonElement Raw { get; init; }
}
