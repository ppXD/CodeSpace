using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Core.Services.Workflows.RunSources.Admission.Exceptions;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.RunSources.Admission;

/// <summary>
/// Admits or refuses one activation's run for a pull request an outsider can cause — opened, reopened, new commits.
/// A fork PR from a stranger on a public repository used to start a run like any member's, and every close/reopen
/// started another. Now the activation's authors filter decides the first — on the author, and on whoever pushed new
/// commits — and a short per-(activation, PR, head) debounce the second. Every refusal costs only this activation's run.
/// </summary>
public sealed class PullRequestTriggerAdmission : IPullRequestTriggerAdmission, IScopedDependency
{
    /// <summary>
    /// How long one run from an activation holds off the next for the same PR at the same head commit. A close/reopen loop
    /// re-sends a head that already ran, and that is what this holds off; a push moves the head, so new commits always
    /// start their own run, however soon after the last. Leading-edge: the first event for a head is the one that runs.
    /// </summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(60);

    /// <summary>How long dispatch waits on the provider for a user's standing. Webhook senders time out at about ten seconds, and an unanswered lookup reads as not a member.</summary>
    internal static readonly TimeSpan AuthorLookupTimeout = TimeSpan.FromSeconds(5);

    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _providers;
    private readonly IWebhookClaimStore _claims;
    private readonly ILogger<PullRequestTriggerAdmission> _logger;

    /// <summary>The event whose users this scope has asked the provider about, and their answers — once per user per delivery, however many activations match it.</summary>
    private IPullRequestOriginEvent? _standingsFor;
    private readonly Dictionary<string, PullRequestAuthorAssociation> _standings = new();

    public PullRequestTriggerAdmission(CodeSpaceDbContext db, IProviderRegistry providers, IWebhookClaimStore claims, ILogger<PullRequestTriggerAdmission> logger)
    {
        _db = db;
        _providers = providers;
        _claims = claims;
        _logger = logger;
    }

    public async Task EnsureAdmittedAsync(WorkflowActivation activation, NormalizedEvent normalizedEvent, JsonElement activationConfig, CancellationToken cancellationToken)
    {
        if (normalizedEvent is not IPullRequestOriginEvent pullRequest) return;

        await CompleteAuthorAssociationAsync(pullRequest, cancellationToken).ConfigureAwait(false);

        var required = PullRequestTriggerAuthors.Required(activationConfig, pullRequest.Origin);

        EnsureAuthorAdmitted(activation, pullRequest, required);
        await EnsurePusherAdmittedAsync(activation, pullRequest, required, cancellationToken).ConfigureAwait(false);
        await EnsureNotDebouncedAsync(activation, pullRequest, normalizedEvent.ProviderEventId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One key per (activation, pull request, head): the activation's run for this head holds it for <see cref="DebounceWindow"/>.</summary>
    internal static string BuildDebounceKey(Guid activationId, Guid repositoryId, int number, string? headSha) => $"pr-debounce:{activationId:N}:{repositoryId:N}:{number}:{headSha}";

    /// <summary>
    /// One refusal row per (activation, PR, kind) per day. An outsider's reopen loop is a stream of genuinely signed
    /// deliveries, so a row per delivery would bury every other refusal in the operator's list just as forged ones did.
    /// </summary>
    internal static string BuildRefusalAuditKey(string kind, Guid activationId, IPullRequestOriginEvent pullRequest, DateTimeOffset now) =>
        $"rejected:{kind}:{activationId:N}:{pullRequest.RepositoryId:N}:{pullRequest.Number}:{now.UtcTicks / WebhookIngestionService.RefusalAuditWindow.Ticks}";

    private void EnsureAuthorAdmitted(WorkflowActivation activation, IPullRequestOriginEvent pullRequest, string required)
    {
        if (PullRequestTriggerAuthors.Admits(required, pullRequest.Origin.AuthorAssociation)) return;

        throw new PullRequestAuthorRefusedException(AuthorRefusal(pullRequest), BuildRefusalAuditKey("author", activation.Id, pullRequest, DateTimeOffset.UtcNow));
    }

    /// <summary>The pusher's standing is asked only when the rule needs it: someone other than the author pushed to a fork head.</summary>
    private async Task EnsurePusherAdmittedAsync(WorkflowActivation activation, IPullRequestOriginEvent pullRequest, string required, CancellationToken cancellationToken)
    {
        if (PullRequestTriggerAuthors.AdmitsPusher(required, pullRequest.Origin)) return;

        await CompletePusherAssociationAsync(pullRequest, cancellationToken).ConfigureAwait(false);

        if (PullRequestTriggerAuthors.AdmitsPusher(required, pullRequest.Origin)) return;

        throw new PullRequestAuthorRefusedException(PusherRefusal(pullRequest), BuildRefusalAuditKey("pusher", activation.Id, pullRequest, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The delivery id holds the claim, so two activations' claims for one delivery never collide and a concurrent copy of
    /// the same delivery passes here to be deduplicated by the run's idempotency key. A delivery posted again after its run
    /// started never gets here: dispatch skips it as already started, so it cannot restart the window.
    /// </summary>
    private async Task EnsureNotDebouncedAsync(WorkflowActivation activation, IPullRequestOriginEvent pullRequest, string deliveryId, CancellationToken cancellationToken)
    {
        var claimed = await _claims.TryClaimAsync(BuildDebounceKey(activation.Id, pullRequest.RepositoryId, pullRequest.Number, pullRequest.HeadSha), deliveryId, DebounceWindow, cancellationToken).ConfigureAwait(false);

        if (claimed) return;

        throw new PullRequestTriggerDebouncedException(pullRequest.Number, pullRequest.HeadSha, DebounceWindow, $"{BuildRefusalAuditKey("debounce", activation.Id, pullRequest, DateTimeOffset.UtcNow)}:{pullRequest.HeadSha}");
    }

    private static string AuthorRefusal(IPullRequestOriginEvent pullRequest) =>
        $"pull request #{pullRequest.Number} was written by an author whose standing is '{Describe(pullRequest.Origin.AuthorAssociation)}', and this trigger admits only members — set 'Pull requests from' to Anyone to admit every author";

    private static string PusherRefusal(IPullRequestOriginEvent pullRequest) =>
        $"new commits on pull request #{pullRequest.Number} were pushed by user {pullRequest.Origin.PusherExternalId}, not its author, to a head in another repository, and their standing is '{Describe(pullRequest.Origin.PusherAssociation)}' — this trigger admits only members' code; set 'Pull requests from' to Anyone to admit it";

    private static string Describe(PullRequestAuthorAssociation association) => association.ToString().ToLowerInvariant();

    /// <summary>
    /// GitLab's payload names the author but not their standing; ask the provider, once per delivery, when it can be
    /// asked. A provider that cannot answer leaves the standing Unknown, which no members-only trigger admits.
    /// </summary>
    private async Task CompleteAuthorAssociationAsync(IPullRequestOriginEvent pullRequest, CancellationToken cancellationToken)
    {
        if (pullRequest.Origin.AuthorAssociation != PullRequestAuthorAssociation.Unknown || pullRequest.Origin.AuthorExternalId == null) return;

        var standing = await StandingOfAsync(pullRequest, pullRequest.Origin.AuthorExternalId, cancellationToken).ConfigureAwait(false);

        pullRequest.Origin = pullRequest.Origin with { AuthorAssociation = standing };
    }

    /// <summary>Neither provider puts the pusher's standing on the payload. GitLab can be asked; GitHub cannot, so its pusher stays Unknown.</summary>
    private async Task CompletePusherAssociationAsync(IPullRequestOriginEvent pullRequest, CancellationToken cancellationToken)
    {
        if (pullRequest.Origin.PusherAssociation != PullRequestAuthorAssociation.Unknown || pullRequest.Origin.PusherExternalId == null) return;

        var standing = await StandingOfAsync(pullRequest, pullRequest.Origin.PusherExternalId, cancellationToken).ConfigureAwait(false);

        pullRequest.Origin = pullRequest.Origin with { PusherAssociation = standing };
    }

    /// <summary>A user's standing on this event's repository, asked at most once per delivery — an unanswered lookup costs up to its timeout, and every matching activation repeating it would outlast the provider's own.</summary>
    private async Task<PullRequestAuthorAssociation> StandingOfAsync(IPullRequestOriginEvent pullRequest, string userExternalId, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_standingsFor, pullRequest)) _standings.Clear();

        _standingsFor = pullRequest;

        if (_standings.TryGetValue(userExternalId, out var known)) return known;

        var standing = await LookUpStandingAsync(pullRequest.RepositoryId, userExternalId, cancellationToken).ConfigureAwait(false);

        _standings[userExternalId] = standing;
        return standing;
    }

    private async Task<PullRequestAuthorAssociation> LookUpStandingAsync(Guid repositoryId, string userExternalId, CancellationToken cancellationToken)
    {
        var repository = await _db.Repository.AsNoTracking().Include(r => r.ProviderInstance).Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId, cancellationToken).ConfigureAwait(false);

        if (repository?.Credential == null) return PullRequestAuthorAssociation.Unknown;
        if (!_providers.TryGet<IRepositoryMemberStandingCapability>(repository.ProviderInstance.Provider, out var standing) || standing == null) return PullRequestAuthorAssociation.Unknown;

        return await AskProviderAsync(standing, repository, userExternalId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PullRequestAuthorAssociation> AskProviderAsync(IRepositoryMemberStandingCapability standing, Repository repository, string userExternalId, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AuthorLookupTimeout);

        try
        {
            return await standing.GetMemberStandingAsync(new ProviderContext(repository.ProviderInstance, repository.Credential!), repository.ToRemoteRepository(), userExternalId, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the standing of user {UserExternalId} on repository {RepositoryId}; a members-only trigger will not admit them", userExternalId, repository.Id);
            return PullRequestAuthorAssociation.Unknown;
        }
    }
}
