using CodeSpace.Core.Authorization;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Api.Filters;

/// <summary>
/// Maps domain exceptions to HTTP status codes the frontend can branch on. The default
/// catch-all returns 500 with a masked message — no stack trace, no SQL text leaks. Logged
/// with full detail server-side so operators retain debuggability.
/// </summary>
public sealed class GlobalExceptionFilter : IExceptionFilter
{
    private readonly ILogger<GlobalExceptionFilter> _logger;

    public GlobalExceptionFilter(ILogger<GlobalExceptionFilter> logger) { _logger = logger; }

    public void OnException(ExceptionContext context)
    {
        var path = context.HttpContext.Request.Path;

        switch (context.Exception)
        {
            case TenantAccessDeniedException tenant:
                _logger.LogWarning("Tenant access denied at {Path}: user={UserId} team={TeamId} reason={Reason}", path, tenant.UserId, tenant.TeamId, tenant.Reason);
                context.Result = BuildProblemResult(StatusCodes.Status403Forbidden, "forbidden", "Access denied for this tenant.");
                break;

            case UnauthorizedAccessException:
                _logger.LogWarning("Unauthorized at {Path}: {Message}", path, context.Exception.Message);
                context.Result = BuildProblemResult(StatusCodes.Status401Unauthorized, "unauthorized", "Authentication required.");
                break;

            case InvalidCredentialsException:
                // Same status as a bearer-token failure; same generic message — no email enumeration.
                _logger.LogInformation("Invalid credentials at {Path}", path);
                context.Result = BuildProblemResult(StatusCodes.Status401Unauthorized, "invalid_credentials", "Invalid email or password.");
                break;

            case OAuthCallbackException oauthCallback:
                _logger.LogWarning("OAuth callback rejected at {Path}: {Reason}", path, oauthCallback.Reason);
                context.Result = BuildProblemResult(StatusCodes.Status400BadRequest, "oauth_callback_invalid", oauthCallback.Reason);
                break;

            case OAuthExchangeException oauthExchange:
                _logger.LogWarning("OAuth exchange failed at {Path}: error={Error} description={Description}", path, oauthExchange.Error, oauthExchange.Description);
                context.Result = BuildProblemResult(StatusCodes.Status400BadRequest, "oauth_exchange_failed", oauthExchange.Message);
                break;

            case ProviderRateLimitedException rateLimit:
                _logger.LogWarning("Provider rate limited at {Path}: instance={InstanceId} op={Operation}", path, rateLimit.ProviderInstanceId, rateLimit.OperationName);
                context.Result = BuildProblemResult(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests to the upstream provider. Retry shortly.");
                break;

            case ActorIdentityRequiredException actorReq:
                // 428 Precondition Required = "do something first, then retry". The frontend's global
                // interceptor branches on code=actor_identity_required to open the identity-link modal
                // for the named provider — one signal, reused by every act-as-user operation.
                _logger.LogInformation("Actor identity required at {Path}: provider={Provider} instance={InstanceId}", path, actorReq.ProviderKind, actorReq.ProviderInstanceId);
                context.Result = new ObjectResult(new
                {
                    code = "actor_identity_required",
                    message = actorReq.Message,
                    provider = actorReq.ProviderKind.ToString(),
                    providerInstanceId = actorReq.ProviderInstanceId
                })
                {
                    StatusCode = StatusCodes.Status428PreconditionRequired
                };
                break;

            case ActorRepoPermissionDeniedException repoPerm:
                // 403 Forbidden = "you can't do this". The identity is fine (not a 428) but the responder
                // lacks access on this repo. The frontend branches on code=actor_repo_permission_denied to
                // show the reason inline on the card and leave it Open — no false success.
                _logger.LogWarning("Actor repo permission denied at {Path}: provider={Provider} instance={InstanceId} repo={Repo}", path, repoPerm.ProviderKind, repoPerm.ProviderInstanceId, repoPerm.RepositoryPath);
                context.Result = new ObjectResult(new
                {
                    code = "actor_repo_permission_denied",
                    message = repoPerm.Reason ?? $"You don't have permission to act on {repoPerm.RepositoryPath}.",
                    provider = repoPerm.ProviderKind.ToString(),
                    providerInstanceId = repoPerm.ProviderInstanceId,
                    repository = repoPerm.RepositoryPath
                })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
                break;

            case ProviderInsufficientScopeException scope:
                // 422 Unprocessable Entity = "syntactically valid but semantically blocked".
                // The frontend branches on code=oauth_insufficient_scope to render a
                // remediation card naming the exact scope(s) the operator must add.
                _logger.LogWarning("Provider {Provider} insufficient scope at {Path}: capability={Capability} missing={Missing}", scope.ProviderKind, path, scope.CapabilityName, string.Join(", ", scope.MissingScopes));
                context.Result = BuildScopeProblemResult(scope);
                break;

            case ProviderApiException providerAuth when providerAuth.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden:
                // A provider REJECTING the picked credential (bad/expired token, or missing
                // org/repo access) is NOT an app-session failure. Mirroring its 401/403 made the
                // SPA mistake it for an expired login and sign the user out mid-flow. Surface it
                // as 422 ("the chosen credential is unprocessable for this op") with an actionable
                // message; the provider's real status rides along in the body for the UI.
                _logger.LogWarning("Provider {Provider} rejected credential (HTTP {Status}) at {Path}: operation={Operation} message={Message}", providerAuth.ProviderKind, providerAuth.StatusCode, path, providerAuth.OperationName, providerAuth.ProviderMessage);
                context.Result = new ObjectResult(new
                {
                    code = "provider_unauthorized",
                    message = BuildProviderApiMessage(providerAuth),
                    provider = providerAuth.ProviderKind.ToString(),
                    providerStatus = providerAuth.StatusCode,
                })
                {
                    StatusCode = StatusCodes.Status422UnprocessableEntity,
                };
                break;

            case ProviderApiException providerApi:
                // Mirror the upstream provider's status code so the SPA gets a real 4xx —
                // before this case existed, any Octokit.NotFoundException / GitLabException
                // fell through to the default arm and surfaced as a useless 500. (401/403 are
                // handled by the arm above so they never sign the user out.)
                _logger.LogWarning("Provider {Provider} HTTP {Status} at {Path}: operation={Operation} message={Message}", providerApi.ProviderKind, providerApi.StatusCode, path, providerApi.OperationName, providerApi.ProviderMessage);
                context.Result = BuildProblemResult(providerApi.StatusCode, "provider_error", BuildProviderApiMessage(providerApi));
                break;

            case KeyNotFoundException:
                _logger.LogInformation("Not found at {Path}: {Message}", path, context.Exception.Message);
                context.Result = BuildProblemResult(StatusCodes.Status404NotFound, "not_found", "The requested resource was not found.");
                break;

            case WorkflowValidationException workflowValidation:
                // 422 Unprocessable Entity is the right code for "JSON parsed, body shape
                // accepted, domain rules say no". The workflow editor inspector renders one
                // banner per error so we return the full list rather than collapsing it.
                _logger.LogWarning("Workflow definition rejected at {Path}: {ErrorCount} error(s) {Errors}", path, workflowValidation.Errors.Count, string.Join(" | ", workflowValidation.Errors));
                context.Result = new ObjectResult(new
                {
                    code = "workflow_definition_invalid",
                    message = workflowValidation.Message,
                    errors = workflowValidation.Errors,
                })
                {
                    StatusCode = StatusCodes.Status422UnprocessableEntity,
                };
                break;

            case DbUpdateException dbEx when ExtractPostgresSqlState(dbEx) == "23505":
                // Postgres unique-constraint violation. Handlers SHOULD pre-check and throw
                // a friendlier InvalidOperationException; this is the safety net for race
                // conditions (concurrent inserts that both passed the pre-check) and for
                // any handler that hasn't been wired with a pre-check yet.
                _logger.LogWarning(dbEx, "Unique constraint violation at {Path}", path);
                context.Result = BuildProblemResult(StatusCodes.Status409Conflict, "duplicate_resource", "A resource with the same identifying fields already exists.");
                break;

            case RerunTargetNotFoundException:
                // 400 — the chosen from-node is invalid (unknown id, or a container-internal node). The message
                // names the node / the container to rerun instead, so surface it verbatim (unlike the generic 404).
                _logger.LogInformation("Rerun target invalid at {Path}: {Message}", path, context.Exception.Message);
                context.Result = BuildProblemResult(StatusCodes.Status400BadRequest, "rerun_target_invalid", context.Exception.Message);
                break;

            case RerunBlockedByUnsupportedNodeException blocked:
                // 422 — valid request, blocked by a domain rule: the re-run closure contains a node whose rerun
                // isn't supported yet (a suspendable agent/subworkflow/supervisor node, or a Map/Loop/Try
                // container). Side-effecting nodes are NOT blocked here — the engine approval-gates them at
                // runtime. The message names the offending node(s) so the operator knows where to rerun-from.
                _logger.LogInformation("Rerun blocked by unsupported node at {Path}: nodes={Nodes}", path, string.Join(", ", blocked.BlockedNodeIds));
                context.Result = BuildProblemResult(StatusCodes.Status422UnprocessableEntity, "rerun_blocked_unsupported_node", blocked.Message);
                break;

            case RerunUpstreamNotReusableException:
                _logger.LogInformation("Rerun upstream not reusable at {Path}: {Message}", path, context.Exception.Message);
                context.Result = BuildProblemResult(StatusCodes.Status422UnprocessableEntity, "rerun_upstream_not_reusable", context.Exception.Message);
                break;

            case InvalidOperationException invalid:
                _logger.LogWarning(context.Exception, "Invalid operation at {Path}", path);
                context.Result = BuildProblemResult(StatusCodes.Status400BadRequest, "invalid_request", invalid.Message);
                break;

            default:
                _logger.LogError(context.Exception, "Unhandled exception at {Path}", path);
                context.Result = BuildProblemResult(StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.");
                break;
        }

        context.ExceptionHandled = true;
    }

    private static ObjectResult BuildProblemResult(int statusCode, string code, string message)
    {
        return new ObjectResult(new { code, message }) { StatusCode = statusCode };
    }

    /// <summary>
    /// Phrasing rule: prefer the actionable explanation for the common cases (404 / 403),
    /// fall back to the raw provider message otherwise. The provider's own text is appended
    /// after the explanation so operators copying the message into a support ticket still
    /// see what the upstream actually said.
    /// </summary>
    private static string BuildProviderApiMessage(ProviderApiException ex)
    {
        var providerName = ex.ProviderKind.ToString();

        var hint = ex.StatusCode switch
        {
            404 => $"{providerName} couldn't find this resource. It may have been renamed, deleted, or your credential lost access. Try re-linking the repository.",
            403 => $"{providerName} refused the request. Your credential may be missing access — re-authorise with the right org/repo permissions.",
            401 => $"{providerName} rejected the credential. Reconnect to refresh the token.",
            _ => $"{providerName} returned an error."
        };

        return $"{hint} ({ex.ProviderMessage})";
    }

    /// <summary>
    /// Walks the exception chain looking for an <see cref="NpgsqlException"/> with a SqlState.
    /// EF wraps the provider exception inside <c>DbUpdateException.InnerException</c>; some
    /// retry strategies wrap further. Returns null for non-Postgres providers (sqlite test runs).
    /// </summary>
    private static string? ExtractPostgresSqlState(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is PostgresException pg) return pg.SqlState;
        }

        return null;
    }

    /// <summary>
    /// Returns a structured 422 with the scope details the frontend needs to render the
    /// remediation UI. Keeps the generic <c>code</c>/<c>message</c> envelope so the SPA's
    /// error handler still recognises the shape, while adding <c>provider</c> /
    /// <c>capability</c> / <c>missingScopes</c> / <c>grantedScopes</c> for the specific path.
    /// </summary>
    private static ObjectResult BuildScopeProblemResult(ProviderInsufficientScopeException scope)
    {
        var providerName = Enum.GetName(typeof(ProviderKind), scope.ProviderKind) ?? scope.ProviderKind.ToString();
        var message = $"Your {providerName} credential is missing scope(s): {string.Join(", ", scope.MissingScopes)}. Re-connect after granting them.";

        return new ObjectResult(new
        {
            code = "oauth_insufficient_scope",
            message,
            provider = providerName,
            capability = scope.CapabilityName,
            missingScopes = scope.MissingScopes,
            grantedScopes = scope.GrantedScopes
        })
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity
        };
    }
}
