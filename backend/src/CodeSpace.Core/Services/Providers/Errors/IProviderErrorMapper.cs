using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;

namespace CodeSpace.Core.Services.Providers.Errors;

/// <summary>
/// Translates a provider SDK's "insufficient scope" error (which comes back as some opaque
/// 403/401/422 with provider-specific shape) into our typed
/// <see cref="ProviderInsufficientScopeException"/>. One implementation per provider — each
/// knows its SDK's exception types and response shape.
///
/// Returns null when the exception is NOT an insufficient-scope error; caller propagates the
/// original. Keep this purely concerned with scope detection — non-scope auth failures (bad
/// token, revoked, expired) and transport errors stay on their normal exception paths.
/// </summary>
public interface IProviderErrorMapper
{
    ProviderKind Kind { get; }

    /// <summary>
    /// Inspect the exception. If it represents an "insufficient scope" failure, return a
    /// fully-populated typed exception. Otherwise return null so the caller can re-throw the
    /// original. Implementations MUST NOT throw.
    /// </summary>
    ProviderInsufficientScopeException? TryMapInsufficientScope(Exception exception, string operationName);
}
