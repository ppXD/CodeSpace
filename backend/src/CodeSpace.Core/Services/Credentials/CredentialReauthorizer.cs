using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Messages.Credentials;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// The pipeline of <see cref="ICredentialReauthorizer"/>: each step enriches <c>_ctx</c> or throws the exception that names
/// why nothing was changed. Nothing is written until the new authorization has been sealed, probed and compared with the
/// account the credential belongs to; the one durable write happens under the refresh lock; what follows is tracked.
/// </summary>
public sealed partial class CredentialReauthorizer : ICredentialReauthorizer, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IBoundedCredentialProbe _probe;
    private readonly IPayloadEncryptor _encryptor;
    private readonly ICredentialPayloadSerializer _serializer;
    private readonly ICredentialPayloadReader _payloadReader;
    private readonly ICrossProcessLock _crossProcessLock;
    private readonly IKeptOAuthPairs _keptPairs;
    private readonly ICredentialReauthorizationWriter _writer;
    private readonly ICredentialBindingReviver _bindings;
    private readonly IUserProviderIdentityService _identities;
    private readonly IOAuthGrantRevoker _grants;
    private readonly IPostCommitActions _postCommit;
    private readonly ILogger<CredentialReauthorizer> _logger;

    private ReauthorizationContext _ctx = default!;

    public CredentialReauthorizer(CodeSpaceDbContext db, IProviderRegistry registry, IBoundedCredentialProbe probe, IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer, ICredentialPayloadReader payloadReader, ICrossProcessLock crossProcessLock, IKeptOAuthPairs keptPairs, ICredentialReauthorizationWriter writer, ICredentialBindingReviver bindings, IUserProviderIdentityService identities, IOAuthGrantRevoker grants, IPostCommitActions postCommit, ILogger<CredentialReauthorizer> logger)
    {
        _db = db;
        _registry = registry;
        _probe = probe;
        _encryptor = encryptor;
        _serializer = serializer;
        _payloadReader = payloadReader;
        _crossProcessLock = crossProcessLock;
        _keptPairs = keptPairs;
        _writer = writer;
        _bindings = bindings;
        _identities = identities;
        _grants = grants;
        _postCommit = postCommit;
        _logger = logger;
    }

    public async Task<CredentialReauthorizationResult> ReauthorizeAsync(CredentialReauthorization reauthorization, CancellationToken cancellationToken)
    {
        _ctx = BuildContext(reauthorization);

        await LoadCredentialAsync(cancellationToken).ConfigureAwait(false);          // KeyNotFoundException, ArgumentException
        await EnsureNotRetiredAsync(cancellationToken).ConfigureAwait(false);        // CredentialRetiredException
        SealNewPayload();

        await ProbeNewAuthorizationAsync(cancellationToken).ConfigureAwait(false);
        EnsureNewAuthorizationIsUsable();                                            // InvalidOperationException
        await LoadKnownAccountAsync(cancellationToken).ConfigureAwait(false);
        EnsureSameProviderAccount();                                                 // CredentialAccountMismatchException

        await ReplaceUnderRefreshLockAsync(cancellationToken).ConfigureAwait(false); // CredentialRetiredException
        await ReviveBindingsAsync(cancellationToken).ConfigureAwait(false);
        await RelinkActorIdentityBestEffortAsync(cancellationToken).ConfigureAwait(false);
        await RevokeReplacedTokensAfterCommitAsync(cancellationToken).ConfigureAwait(false);

        return BuildResult();
    }
}
