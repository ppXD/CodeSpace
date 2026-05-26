namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// A single label on a pull/merge request — name + the provider's chosen colour so the
/// UI can render the same coloured pills the operator sees on GitHub / GitLab natively.
/// </summary>
/// <remarks>
/// <see cref="Color"/> is the raw hex string from the provider, WITHOUT the leading
/// <c>#</c> (e.g. <c>"f29513"</c>). GitHub gives this directly via Octokit's
/// <c>Label.Color</c>; GitLab gives <c>#f29513</c> via the project-labels endpoint and
/// the provider strips the prefix before populating this field. Null when the provider
/// either didn't expose a colour (rare) or the colour lookup failed (GitLab labels API
/// unreachable / token lacks read_repository — the SPA falls back to a name-hash palette
/// so the pill is still distinguishable).
/// </remarks>
public sealed record LabelRef
{
    public required string Name { get; init; }
    public string? Color { get; init; }
}
