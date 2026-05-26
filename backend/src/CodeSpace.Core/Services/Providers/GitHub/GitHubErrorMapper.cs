using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Octokit;

namespace CodeSpace.Core.Services.Providers.GitHub;

/// <summary>
/// Maps Octokit's <see cref="ForbiddenException"/> / <see cref="AuthorizationException"/> to
/// <see cref="ProviderInsufficientScopeException"/>. GitHub returns 403 with response headers
/// that pin down what scope(s) the token has and what was missing:
///   • <c>X-OAuth-Scopes</c>: actual granted scopes (comma-separated)
///   • <c>X-Accepted-OAuth-Scopes</c>: scopes the endpoint needs (any one of)
///
/// When both headers are present the caller gets a precise "you have X, you need Y" message.
/// When headers are absent we fall back to message parsing — Octokit surfaces "Resource not
/// accessible by personal access token" / "Must have admin rights" in <see cref="Exception.Message"/>.
/// </summary>
public sealed class GitHubErrorMapper : IProviderErrorMapper, ISingletonDependency
{
    public ProviderKind Kind => ProviderKind.GitHub;

    public ProviderInsufficientScopeException? TryMapInsufficientScope(Exception exception, string operationName)
    {
        if (exception is not ApiException api) return null;

        var statusCode = (int)api.StatusCode;

        // 401 = bad / revoked token (different path — InvalidCredentials).
        // 403 = authenticated but forbidden. Insufficient_scope lives here.
        if (statusCode != 403) return null;

        var grantedHeader = api.HttpResponse?.Headers?.GetValueOrDefault("X-OAuth-Scopes");
        var acceptedHeader = api.HttpResponse?.Headers?.GetValueOrDefault("X-Accepted-OAuth-Scopes");

        var granted = ParseScopes(grantedHeader);
        var accepted = ParseScopes(acceptedHeader);

        // The most-reliable signal: GitHub explicitly told us which scope(s) are accepted.
        if (accepted.Count > 0)
        {
            // Missing = accepted scopes the token doesn't have. Filter out any the token DOES have.
            var missing = accepted.Where(s => !granted.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();

            if (missing.Count == 0)
            {
                // Token has at least one accepted scope but still 403 — could be SAML/SSO, org policy,
                // or fine-grained PAT restriction. Not really an insufficient_scope. Pass through.
                return null;
            }

            return new ProviderInsufficientScopeException(Kind, operationName, missing, granted, api.Message);
        }

        // Fallback heuristic: 403 + message hints at scope. Best-effort guess at what's missing.
        if (LooksLikeScopeIssue(api.Message))
        {
            return new ProviderInsufficientScopeException(Kind, operationName, new[] { "repo" }, granted, api.Message);
        }

        return null;
    }

    private static IReadOnlyList<string> ParseScopes(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return Array.Empty<string>();

        return header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool LooksLikeScopeIssue(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;

        // Octokit / GitHub phrases. "Resource not accessible by integration" / "personal access
        // token" both indicate the token doesn't have the right scope or permission level.
        return message.Contains("not accessible", StringComparison.OrdinalIgnoreCase)
               || message.Contains("scope", StringComparison.OrdinalIgnoreCase)
               || message.Contains("must have admin", StringComparison.OrdinalIgnoreCase);
    }
}
