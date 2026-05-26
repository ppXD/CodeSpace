namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Thrown when the callback can't be completed for a reason that's the caller's fault —
/// missing/expired/replayed state, malformed provider instance, etc. Mapped by
/// <c>GlobalExceptionFilter</c> to 400 Bad Request.
/// </summary>
public sealed class OAuthCallbackException : Exception
{
    public OAuthCallbackException(string reason) : base(reason) { Reason = reason; }
    public string Reason { get; }
}
