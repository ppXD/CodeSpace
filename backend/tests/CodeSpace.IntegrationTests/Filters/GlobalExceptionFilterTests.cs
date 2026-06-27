using System.Text.Json;
using CodeSpace.Api.Filters;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Filters;

/// <summary>
/// Pins GlobalExceptionFilter's provider-error mapping. The critical contract: a provider
/// REJECTING the picked credential (HTTP 401/403 from GitHub/GitLab) must surface as 422
/// "provider_unauthorized", NOT an app 401 — the SPA signs the user out on any 401, so
/// mirroring the provider's 401 used to drop the user's session mid "Add repository". Other
/// provider statuses (404 / 5xx) still mirror through as "provider_error".
///
/// Lives in IntegrationTests (not UnitTests) because the filter needs the ASP.NET framework +
/// CodeSpace.Api references the lean UnitTests project excludes; tagged Integration per the
/// one-tier-per-project rule (TESTING.md) even though it touches no database (no Postgres collection).
/// </summary>
[Trait("Category", "Integration")]
public class GlobalExceptionFilterTests
{
    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    public void Provider_credential_rejection_maps_to_422_not_signout(int providerStatus)
    {
        var result = Run(new ProviderApiException(ProviderKind.GitLab, providerStatus, "ListAccessibleRepositoriesAsync", "rejected", new Exception("inner")));
        var body = Body(result);

        result.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        body.GetProperty("code").GetString().ShouldBe("provider_unauthorized");
        body.GetProperty("providerStatus").GetInt32().ShouldBe(providerStatus);
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    public void Other_provider_statuses_mirror_through_as_provider_error(int providerStatus)
    {
        var result = Run(new ProviderApiException(ProviderKind.GitHub, providerStatus, "ListPullRequestsAsync", "boom", new Exception("inner")));

        result.StatusCode.ShouldBe(providerStatus);
        Body(result).GetProperty("code").GetString().ShouldBe("provider_error");
    }

    [Fact]
    public void Provider_unauthorized_body_names_the_provider_and_carries_an_actionable_message()
    {
        var result = Run(new ProviderApiException(ProviderKind.GitLab, StatusCodes.Status401Unauthorized, "ListAccessibleRepositoriesAsync", "401 Unauthorized", new Exception("inner")));
        var body = Body(result);

        body.GetProperty("provider").GetString().ShouldBe("GitLab");
        body.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Insufficient_scope_still_maps_to_its_structured_422_unchanged()
    {
        // Regression guard: the new ProviderApiException 401/403 arm sits right beside the scope
        // arm — prove it didn't shadow ProviderInsufficientScopeException's structured response.
        var result = Run(new ProviderInsufficientScopeException(ProviderKind.GitLab, "IRepositoryCatalogCapability", new[] { "api" }, new[] { "read_user" }));
        var body = Body(result);

        result.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        body.GetProperty("code").GetString().ShouldBe("oauth_insufficient_scope");
        body.GetProperty("missingScopes")[0].GetString().ShouldBe("api");
    }

    [Fact]
    public void Actor_identity_required_maps_to_428_with_the_provider_so_the_frontend_can_prompt_a_link()
    {
        // The single signal every act-as-user operation funnels through: the SPA's global
        // interceptor branches on code=actor_identity_required to open the link modal.
        var instanceId = Guid.NewGuid();
        var result = Run(new ActorIdentityRequiredException(ProviderKind.GitLab, instanceId));
        var body = Body(result);

        result.StatusCode.ShouldBe(StatusCodes.Status428PreconditionRequired);
        body.GetProperty("code").GetString().ShouldBe("actor_identity_required");
        body.GetProperty("provider").GetString().ShouldBe("GitLab");
        body.GetProperty("providerInstanceId").GetGuid().ShouldBe(instanceId);
    }

    [Fact]
    public void Argument_exception_maps_to_400_invalid_request_not_a_masked_500()
    {
        // A caller-supplied value that fails validation (an out-of-range launch cap, related repos passed without
        // a primary) throws ArgumentException — it must surface as 400 with the real reason, NOT the masked 500
        // default arm. Regression guard for the launch-path "invalid input → useless 500" bug.
        var result = Run(new ArgumentException("MaxParallelism must be >= 1.", "maxParallelism"));
        var body = Body(result);

        result.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        body.GetProperty("code").GetString().ShouldBe("invalid_request");
        body.GetProperty("message").GetString()!.ShouldContain("MaxParallelism must be >= 1.");
    }

    [Fact]
    public void Argument_subtypes_also_map_to_400()
    {
        // ArgumentNullException / ArgumentOutOfRangeException derive from ArgumentException — the single arm covers them.
        Run(new ArgumentNullException("repositoryId")).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        Run(new ArgumentOutOfRangeException("maxRounds", "must be >= 1")).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void Pack_import_exception_maps_to_400_with_the_actionable_message_not_a_masked_500()
    {
        // The URL pack-preview surface (POST /api/agents/import-preview-url) reaches the egress allowlist + git
        // clone. A non-allowlisted/non-https host or a clone failure throws PackImportException — operator-supplied
        // bad input. It must surface 400 with the actionable reason (which names CODESPACE_PACK_ALLOWED_HOSTS for
        // the host case), NOT the masked 500 default arm that swallows the remediation.
        var result = Run(new PackImportException("Host 'evil.internal' is not in the pack-source allowlist [github.com, gitlab.com]. An operator can add it via CODESPACE_PACK_ALLOWED_HOSTS."));
        var body = Body(result);

        result.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        body.GetProperty("code").GetString().ShouldBe("pack_import_failed");
        body.GetProperty("message").GetString()!.ShouldContain("CODESPACE_PACK_ALLOWED_HOSTS");
    }

    private static ObjectResult Run(Exception exception)
    {
        var filter = new GlobalExceptionFilter(NullLogger<GlobalExceptionFilter>.Instance);
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var context = new ExceptionContext(actionContext, new List<IFilterMetadata>()) { Exception = exception };

        filter.OnException(context);

        context.ExceptionHandled.ShouldBeTrue();
        return context.Result.ShouldBeOfType<ObjectResult>();
    }

    private static JsonElement Body(ObjectResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Value)).RootElement;
}
