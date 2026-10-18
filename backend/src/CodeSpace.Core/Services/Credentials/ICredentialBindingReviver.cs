using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Credentials;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// What a credential coming back to life in place brings back with it. Re-authorizing a credential keeps its id, so nothing
/// bound through it moves; what changes is that the repositories its dead token had turned Error work again, and the group
/// hooks its failures had buried are tried again. A sibling of <see cref="ICredentialSuccessionService"/> rather than another
/// member of it: that one moves bindings between credentials, and nothing moves here.
/// </summary>
public interface ICredentialBindingReviver
{
    /// <summary>
    /// Lift the "needs a new credential" error from every live repository bound through <paramref name="credential"/> and
    /// revive every in-service group hook its failures buried (attempts reset, due now), handing them to the registrar once
    /// the caller's unit of work commits — exactly as a bind or a reconnect revives them. A repository that is Paused or
    /// Unreachable keeps that: it is its own state, not the credential's. Tracked changes only. Returns what came back.
    /// </summary>
    Task<BindingsRevived> ReviveBindingsAsync(Credential credential, CancellationToken cancellationToken);
}
