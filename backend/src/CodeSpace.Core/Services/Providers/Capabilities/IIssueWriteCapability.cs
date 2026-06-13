using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// CREATE an issue on a repository — the write half of the issue surface, gated behind a write role
/// exactly like <see cref="IPullRequestWriteCapability"/>. Rule 7 (ISP): a provider that can't write
/// issues simply doesn't implement this; the registry resolves it by type and the git.create_issue
/// node is unavailable for that provider. Comment / close land later as sibling methods here (every
/// git provider supports all three — same gate).
///
/// <para>A new provider implements just this interface — the registry resolves it by <c>ProviderKind</c>.</para>
/// </summary>
public interface IIssueWriteCapability : IProviderCapability
{
    /// <summary>
    /// Create an issue per <paramref name="input"/> (title + optional body / labels) and return it. The
    /// provider maps the neutral input onto its own API. Throws when the bound credential lacks the
    /// required scope (mapped to 422 with the missing-scope hint) or the repo is invalid (mapped from the
    /// provider's 4xx).
    /// </summary>
    Task<RemoteIssue> CreateIssueAsync(ProviderContext context, RemoteRepository repository, CreateIssueInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Post a comment on issue <paramref name="number"/> and return it. GitHub maps to an issue comment;
    /// GitLab to an issue note. Throws on insufficient scope (mapped to 422) or a provider 4xx (e.g. the
    /// issue doesn't exist / issues are disabled).
    /// </summary>
    Task<RemoteIssueComment> CommentIssueAsync(ProviderContext context, RemoteRepository repository, int number, string body, CancellationToken cancellationToken);

    /// <summary>
    /// Close issue <paramref name="number"/> and return its updated state. GitHub maps to an issue update
    /// (state=closed); GitLab to a state-event edit (state_event=close). Throws on insufficient scope (422)
    /// or a provider 4xx (e.g. the issue doesn't exist). Idempotent at the provider — closing an already-
    /// closed issue is a no-op that still returns the closed issue.
    /// </summary>
    Task<RemoteIssue> CloseIssueAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken);
}
