using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using NGitLab;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// Maps NGitLab's <see cref="GitLabException"/> to <see cref="ProviderInsufficientScopeException"/>.
///
/// GitLab returns 403/401 with either:
///   • A JSON body containing <c>{"error": "insufficient_scope", "scope": "api"}</c> (OAuth-style)
///   • A bare 403 with <c>"403 Forbidden"</c> message (PAT or scope-too-narrow on REST)
///   • A 401 with <c>"401 Unauthorized"</c> — bad token, NOT scope (caller handles separately)
///
/// We map the first two. The token-scope is read from the error body if present; otherwise
/// we guess <c>api</c> (the only scope that covers webhook writes).
/// </summary>
public sealed class GitLabErrorMapper : IProviderErrorMapper, ISingletonDependency
{
    public ProviderKind Kind => ProviderKind.GitLab;

    public ProviderInsufficientScopeException? TryMapInsufficientScope(Exception exception, string operationName)
    {
        if (exception is not GitLabException gl) return null;

        var statusCode = (int)gl.StatusCode;

        if (statusCode != 403) return null;

        var body = gl.ErrorObject?.ToString();
        var hint = gl.ErrorMessage ?? gl.Message;

        // Best-effort parse of GitLab's structured error_description.
        // {"error":"insufficient_scope","error_description":"...","scope":"api"}
        if (!string.IsNullOrEmpty(body) && body.Contains("insufficient_scope", StringComparison.OrdinalIgnoreCase))
        {
            var requiredScope = ExtractRequiredScope(body) ?? "api";
            return new ProviderInsufficientScopeException(Kind, operationName, new[] { requiredScope }, Array.Empty<string>(), hint);
        }

        // 403 without "insufficient_scope" tag: webhook write attempted with read-only token,
        // OAuth token from app missing api scope. Default-guess `api` since it's the umbrella
        // scope covering every write GitLab capability we currently use.
        return new ProviderInsufficientScopeException(Kind, operationName, new[] { "api" }, Array.Empty<string>(), hint);
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
