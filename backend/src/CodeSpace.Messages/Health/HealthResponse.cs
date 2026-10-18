namespace CodeSpace.Messages.Health;

/// <summary>
/// The body of <c>GET /health</c>: this process's build and role, so an API image and a worker image can be compared
/// from outside, and every health check's own finding. Additive to the probe surface — <c>/health/live</c> and
/// <c>/health/ready</c> keep their bodies and status codes.
///
/// <para>Two tiers on one anonymous endpoint. Everyone gets the build, the role and each check's name and status. A signed-in
/// global Admin also gets each check's description and data — the git release, the connection topology, the repair backlog —
/// which on an internet-facing API is reconnaissance, not something to print to a caller who has not signed in.</para>
/// </summary>
/// <param name="Status">The worst check's status: <c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>.</param>
/// <param name="Build">Which build this process is (version plus commit sha), as stamped on every log line.</param>
/// <param name="Role">The Hangfire role the deployment gave this process: <c>Api</c> or <c>Worker</c>.</param>
/// <param name="ProcessesJobs">Whether this process runs Hangfire servers. Every role serves HTTP; only <c>Worker</c> also processes the queue.</param>
/// <param name="DetailsIncluded">Whether each check's <see cref="HealthCheckEntry.Description"/> and <see cref="HealthCheckEntry.Data"/> are filled in: true for a signed-in global Admin, false for everyone else, whose entries carry only a name and a status.</param>
public sealed record HealthResponse(string Status, string Build, string Role, bool ProcessesJobs, bool DetailsIncluded, IReadOnlyList<HealthCheckEntry> Checks);

/// <summary>One health check's finding. <see cref="Data"/> carries the facts behind the status, never a secret; both it and <see cref="Description"/> are empty unless <see cref="HealthResponse.DetailsIncluded"/>.</summary>
public sealed record HealthCheckEntry(string Name, string Status, string? Description, IReadOnlyDictionary<string, object> Data);
