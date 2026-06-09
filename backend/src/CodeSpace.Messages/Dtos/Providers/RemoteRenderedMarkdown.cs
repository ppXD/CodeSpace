namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Markdown rendered to HTML by the provider's own renderer (GitHub/GitLab <c>/markdown</c>) — emoji,
/// @mentions, #issue links, alerts, GFM, and the provider's sanitization, exactly as on their site. The
/// Code tab displays this for READMEs; the client still resolves relative image paths and defensively
/// re-checks the HTML before injecting it.
/// </summary>
public sealed record RemoteRenderedMarkdown
{
    public required string Html { get; init; }
}
