using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

[ApiController]
[Route("api/repositories")]
public class RepositoriesController : ControllerBase
{
    private readonly IMediator _mediator;

    public RepositoriesController(IMediator mediator) { _mediator = mediator; }

    // All endpoints require X-Team-Id header (enforced by TeamMembershipAuthorizationBehavior).
    //
    // Every action binds its Query/Command record directly. ASP.NET model binding fills
    // `[FromQuery]` records from the query string only — when an endpoint also has a route
    // parameter, we take it as `[FromRoute] Guid x` and merge into the record via
    // `query with { ... }`. The URL stays authoritative; the rest of the contract
    // (state, page, perPage, defaults) lives on the record.

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListRepositoriesQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("{repositoryId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid repositoryId, [FromQuery] bool refresh, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetRepositoryQuery { RepositoryId = repositoryId, Refresh = refresh }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost("bind")]
    public async Task<IActionResult> Bind([FromBody] BindRepositoryCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(new { id });
    }

    [HttpPost("bind-bulk")]
    public async Task<IActionResult> BindBulk([FromBody] BindRepositoriesBulkCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpDelete("{repositoryId:guid}")]
    public async Task<IActionResult> Unbind([FromRoute] Guid repositoryId, [FromQuery] Guid? projectId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new UnbindRepositoryCommand { RepositoryId = repositoryId, ProjectId = projectId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("{repositoryId:guid}/test")]
    public async Task<IActionResult> Test([FromRoute] Guid repositoryId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new TestRepositoryBindingCommand { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    // ── Code browser: branches, file tree, file content (the "Code" tab) ──────────────
    // All live reads via the repo's credential — never cached. Membership is enforced by the
    // queries' IRequireRepositoryAccess marker, same as the PR endpoints below.

    /// <summary>All branches for the repository — the Code tab's branch picker, with the default flagged.</summary>
    [HttpGet("{repositoryId:guid}/branches")]
    public async Task<IActionResult> ListBranches([FromRoute] Guid repositoryId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListRepositoryBranchesQuery { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Teammates who can AUTHOR an attributable write on this repo (a live linked identity on its provider) — the actAsUserId picker's source, so it only offers usable authors.</summary>
    [HttpGet("{repositoryId:guid}/act-as-candidates")]
    public async Task<IActionResult> ListActAsCandidates([FromRoute] Guid repositoryId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListActAsCandidatesQuery { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// One level of the file tree. `path` (query) is the repo-root-relative folder — omit for the root;
    /// `ref` is the branch/tag/SHA — omit for the default branch. Non-recursive: the browser drills in lazily.
    /// </summary>
    [HttpGet("{repositoryId:guid}/tree")]
    public async Task<IActionResult> ListTree([FromRoute] Guid repositoryId, [FromQuery] ListRepositoryTreeQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// A single file's content for the viewer. `path` (query) is required; omit `ref` for the default branch.
    /// Binary or oversized files come back flagged with no inline text.
    /// </summary>
    [HttpGet("{repositoryId:guid}/file")]
    public async Task<IActionResult> GetFile([FromRoute] Guid repositoryId, [FromQuery] GetRepositoryFileQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Headline stats for the Code tab's right rail — stars, forks, and (best-effort) commit / branch /
    /// tag / release counts + storage size. Numbers the provider can't supply come back null.
    /// </summary>
    [HttpGet("{repositoryId:guid}/stats")]
    public async Task<IActionResult> GetStats([FromRoute] Guid repositoryId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetRepositoryStatsQuery { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Language composition for the Code tab's Languages bar, descending by percent.</summary>
    [HttpGet("{repositoryId:guid}/languages")]
    public async Task<IActionResult> GetLanguages([FromRoute] Guid repositoryId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetRepositoryLanguagesQuery { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Latest release for the Code tab's Releases card. 200 with a null body when the repo has no releases.</summary>
    [HttpGet("{repositoryId:guid}/releases/latest")]
    public async Task<IActionResult> GetLatestRelease([FromRoute] Guid repositoryId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetLatestReleaseQuery { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Live releases list (newest-first, with notes + assets) for the Releases page. Paginated.</summary>
    [HttpGet("{repositoryId:guid}/releases")]
    public async Task<IActionResult> ListReleases([FromRoute] Guid repositoryId, [FromQuery] ListReleasesQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Live git tags (newest-first) for the Releases page's Tags tab. Paginated.</summary>
    [HttpGet("{repositoryId:guid}/tags")]
    public async Task<IActionResult> ListTags([FromRoute] Guid repositoryId, [FromQuery] ListTagsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Single release by tag for the in-app release-detail page. Tag is a query value to survive slashes/dots.</summary>
    [HttpGet("{repositoryId:guid}/release")]
    public async Task<IActionResult> GetRelease([FromRoute] Guid repositoryId, [FromQuery] string tag, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetReleaseQuery { RepositoryId = repositoryId, Tag = tag }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Latest commit on a path/ref — the Code tab's header bar. `path` (query) omitted ⇒ repo root;
    /// `ref` omitted ⇒ the default branch. 200 with a null body when the path has no history.
    /// </summary>
    [HttpGet("{repositoryId:guid}/commit")]
    public async Task<IActionResult> GetLatestCommit([FromRoute] Guid repositoryId, [FromQuery] GetRepositoryLatestCommitQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Per-entry last commit for a folder's children — the file list's last-commit column. `paths` (repeated
    /// query param) names the entries; `ref` omitted ⇒ default branch. Best-effort + capped: paths that fail
    /// are absent from the returned map (keyed by path).
    /// </summary>
    [HttpGet("{repositoryId:guid}/tree-commits")]
    public async Task<IActionResult> GetTreeCommits([FromRoute] Guid repositoryId, [FromQuery] GetRepositoryTreeCommitsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Render markdown (a README / .md file) to HTML via the repo's provider renderer, in the repo's
    /// context (so #issues, @mentions, relative links resolve). Providers without a render capability
    /// throw NotSupported — the SPA catches the failure and renders the markdown client-side instead.
    /// POST because the markdown body can exceed query-string limits.
    /// </summary>
    [HttpPost("{repositoryId:guid}/render-markdown")]
    public async Task<IActionResult> RenderMarkdown([FromRoute] Guid repositoryId, [FromBody] RenderRepositoryMarkdownQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Live PR/MR fetch — never cached locally. The provider call goes out on every
    /// request, so consumers should debounce / poll politely. `state` accepts the
    /// PullRequestState enum names (Open, Draft, Merged, Closed); omit for "all".
    /// </summary>
    [HttpGet("{repositoryId:guid}/pull-requests")]
    public async Task<IActionResult> ListPullRequests([FromRoute] Guid repositoryId, [FromQuery] ListPullRequestsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Single-PR live fetch. `number` is the per-repo number (#42 on GitHub, !42 on GitLab),
    /// matching the URL shape the user reads in the address bar. Returns the full detail
    /// shape including Body + diff stats.
    /// </summary>
    [HttpGet("{repositoryId:guid}/pull-requests/{number:int}")]
    public async Task<IActionResult> GetPullRequest([FromRoute] Guid repositoryId, [FromRoute] int number, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetPullRequestQuery { RepositoryId = repositoryId, Number = number }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Live commit list for a PR. Ordered oldest-first.</summary>
    [HttpGet("{repositoryId:guid}/pull-requests/{number:int}/commits")]
    public async Task<IActionResult> ListPullRequestCommits([FromRoute] Guid repositoryId, [FromRoute] int number, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListPullRequestCommitsQuery { RepositoryId = repositoryId, Number = number }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Live file-change list for a PR with unified-diff patches. Binary/large files have Patch == null.</summary>
    [HttpGet("{repositoryId:guid}/pull-requests/{number:int}/files")]
    public async Task<IActionResult> ListPullRequestFiles([FromRoute] Guid repositoryId, [FromRoute] int number, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListPullRequestFilesQuery { RepositoryId = repositoryId, Number = number }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Live CI/check-runs list for the PR's HEAD commit — GitHub Actions check_runs or
    /// GitLab pipeline jobs. Returns an empty list when the credential lacks the scope to
    /// read checks (Actions: Read on GitHub, read_api on GitLab) so the PR detail view
    /// degrades gracefully rather than 4xx-ing.
    /// </summary>
    [HttpGet("{repositoryId:guid}/pull-requests/{number:int}/checks")]
    public async Task<IActionResult> ListPullRequestChecks([FromRoute] Guid repositoryId, [FromRoute] int number, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListPullRequestChecksQuery { RepositoryId = repositoryId, Number = number }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Total open + closed PR counts for the repository. One round-trip per provider.</summary>
    [HttpGet("{repositoryId:guid}/pull-requests/counts")]
    public async Task<IActionResult> GetPullRequestCounts([FromRoute] Guid repositoryId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetPullRequestCountsQuery { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Live issue fetch — never cached locally, mirroring the PR list. `state` accepts the IssueState
    /// enum names (Open, Closed); omit for "all". Pull requests are excluded (GitHub returns PRs as
    /// issues — the provider filters them out so this is issues-only).
    /// </summary>
    [HttpGet("{repositoryId:guid}/issues")]
    public async Task<IActionResult> ListIssues([FromRoute] Guid repositoryId, [FromQuery] ListIssuesQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Total open + closed issue counts for the repository. One round-trip per provider.</summary>
    [HttpGet("{repositoryId:guid}/issues/counts")]
    public async Task<IActionResult> GetIssueCounts([FromRoute] Guid repositoryId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetIssueCountsQuery { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Single issue with body + sidebar fields for the in-app detail view. `number` is the per-repo #N / iid.</summary>
    [HttpGet("{repositoryId:guid}/issues/{number:int}")]
    public async Task<IActionResult> GetIssue([FromRoute] Guid repositoryId, [FromRoute] int number, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetIssueQuery { RepositoryId = repositoryId, Number = number }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>User comments on an issue (Conversation), oldest-first.</summary>
    [HttpGet("{repositoryId:guid}/issues/{number:int}/comments")]
    public async Task<IActionResult> ListIssueComments([FromRoute] Guid repositoryId, [FromRoute] int number, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListIssueCommentsQuery { RepositoryId = repositoryId, Number = number }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Activity-timeline events on an issue (assigned / labeled / milestoned / closed / mentioned), oldest-first.</summary>
    [HttpGet("{repositoryId:guid}/issues/{number:int}/events")]
    public async Task<IActionResult> ListIssueEvents([FromRoute] Guid repositoryId, [FromRoute] int number, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListIssueEventsQuery { RepositoryId = repositoryId, Number = number }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Submit a review (Approve / RequestChanges / Comment) back to the PR/MR AS the caller's own
    /// linked identity. Returns 428 actor_identity_required when the caller hasn't linked an identity
    /// for the repo's provider instance, so the SPA can prompt a link and retry. Route's repositoryId
    /// + number are authoritative; the body carries the verdict + optional comment.
    /// </summary>
    [HttpPost("{repositoryId:guid}/pull-requests/{number:int}/review")]
    public async Task<IActionResult> SubmitPullRequestReview([FromRoute] Guid repositoryId, [FromRoute] int number, [FromBody] SubmitPullRequestReviewCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { RepositoryId = repositoryId, Number = number }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Re-point a repository at another active credential of the same provider instance.
    /// Used to recover from a credential disconnect: the operator picks a teammate's
    /// still-valid credential (or their own new one) and the repo flips back to Active.
    ///
    /// Route's repositoryId is authoritative; the body carries NewCredentialId. We bind
    /// route + body separately and merge via `with` so the URL can't be spoofed by a body
    /// that disagrees.
    /// </summary>
    [HttpPost("{repositoryId:guid}/relink-credential")]
    public async Task<IActionResult> RelinkCredential([FromRoute] Guid repositoryId, [FromBody] RelinkRepositoryCredentialCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { RepositoryId = repositoryId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
