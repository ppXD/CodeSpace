using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using NGitLab;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// Maps NGitLab's <see cref="GitLabException"/> to <see cref="ProviderInsufficientScopeException"/>.
///
/// GitLab returns a 403 in two shapes we treat very differently:
///   • Tagged <c>{"error":"insufficient_scope","scope":"api"}</c> (OAuth-style) — a real SCOPE gap;
///     we map it to <see cref="ProviderInsufficientScopeException"/> with the named scope.
///   • A bare <c>"403 Forbidden"</c> — almost always a PERMISSION/membership problem (the actor
///     isn't a project member, their role is too low, or a protected-branch / approval rule blocks
///     them), NOT a scope gap. We DON'T map it (return null) so it falls through to
///     <c>ProviderApiException(403)</c> and the caller renders an accurate "you may lack
///     access/permission" message — rather than a misleading "missing api scope" that sends the
///     user to re-link a token that's already fine.
/// (A 401 is a bad/revoked token — a different path, not handled here.)
/// </summary>
public sealed class GitLabErrorMapper : IProviderErrorMapper, ISingletonDependency
{
    public ProviderKind Kind => ProviderKind.GitLab;

    public ProviderInsufficientScopeException? TryMapInsufficientScope(Exception exception, string operationName)
    {
        if (exception is not GitLabException gl) return null;

        return ClassifyScope((int)gl.StatusCode, gl.ErrorObject?.ToString(), gl.ErrorMessage ?? gl.Message, operationName);
    }

    /// <summary>
    /// Pure classification of a GitLab failure (decoupled from the NGitLab exception type for unit
    /// testability). ONLY a 403 explicitly tagged <c>insufficient_scope</c> is a real scope gap → a
    /// typed exception naming the scope (from the body's <c>"scope":"X"</c>, defaulting to <c>api</c>).
    /// Every other status — including a BARE 403 (a permission/membership problem: not a project
    /// member, role too low, protected-branch / approval rule) — returns null, so the caller falls
    /// through to <c>ProviderApiException(403)</c> and an accurate "you may lack access/permission"
    /// message, rather than a misleading "missing api scope" that sends the user to re-link a fine token.
    /// </summary>
    internal ProviderInsufficientScopeException? ClassifyScope(int statusCode, string? body, string? hint, string operationName)
    {
        if (statusCode != 403) return null;

        if (!string.IsNullOrEmpty(body) && body.Contains("insufficient_scope", StringComparison.OrdinalIgnoreCase))
        {
            var requiredScope = ExtractRequiredScope(body) ?? "api";
            return new ProviderInsufficientScopeException(Kind, operationName, new[] { requiredScope }, Array.Empty<string>(), hint);
        }

        return null;
    }

    /// <summary>
    /// Very small JSON probe — avoids pulling System.Text.Json since the response body has
    /// already been deserialised into an opaque object by NGitLab. Looks for "scope":"X".
    /// </summary>
    private static string? ExtractRequiredScope(string body)
    {
        const string token = "\"scope\":\"";
        var idx = body.IndexOf(token, StringComparison.OrdinalIgnoreCase);

        if (idx < 0) return null;

        var start = idx + token.Length;
        var end = body.IndexOf('"', start);

        return end > start ? body.Substring(start, end - start) : null;
    }
}
