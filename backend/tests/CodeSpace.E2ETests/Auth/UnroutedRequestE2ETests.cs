using System.Net;
using System.Net.Http.Json;
using CodeSpace.E2ETests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// A path this server does not serve says so, and every path it DOES serve still requires a session.
///
/// <para>The second half is the one that must not regress. The global <c>FallbackPolicy</c> exists so
/// that an endpoint someone forgot to mark <c>[Authorize]</c> is refused rather than silently
/// anonymous; answering 404 for unmatched routes must not become a way to reach a matched one.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class UnroutedRequestE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public UnroutedRequestE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    /// <summary>
    /// The exact shape that cost a production diagnosis: an invite link built from a misconfigured
    /// public base URL opens the API host, and <c>/invite/{token}</c> is a route only the SPA has.
    /// </summary>
    [Fact]
    public async Task A_web_app_route_requested_from_the_api_says_there_is_no_such_page()
    {
        var response = await _factory.CreateClient().GetAsync("/invite/some-token");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound,
            customMessage: "An unrouted path answered 401, which reads as 'you need to sign in' when the truth is " +
                           "'this server has never had this page'. That sent a real investigation after an " +
                           "authorization rule on an endpoint that was already anonymous and working.");

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        body!["message"].ShouldContain("no /invite/some-token");
        body["message"].ShouldContain("different origins",
            customMessage: "The message has to name the likely cause. A bare 404 is honest but still leaves the reader guessing.");
    }

    [Fact]
    public async Task An_unknown_path_is_not_found_rather_than_unauthorized()
    {
        var response = await _factory.CreateClient().GetAsync("/nothing-here");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The guard. A real endpoint with no explicit attribute still meets the fallback policy — this
    /// is what stops the change above from turning into anonymous access.
    /// </summary>
    [Fact]
    public async Task A_real_endpoint_still_refuses_an_unauthenticated_caller()
    {
        var response = await _factory.CreateClient().GetAsync("/api/repositories");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            customMessage: "A matched endpoint must still be challenged. If this is 404, the middleware is running " +
                           "for endpoints that DID match and the FallbackPolicy has been bypassed.");
    }

    /// <summary>An anonymous endpoint that exists must still run, not be swallowed as unrouted.</summary>
    [Fact]
    public async Task An_anonymous_endpoint_still_answers()
    {
        var response = await _factory.CreateClient().GetAsync("/api/invitations/not-a-real-token");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        body!["code"].ShouldBe("invitation_not_usable",
            customMessage: "This is the invitation endpoint's OWN 404 about a token, not the middleware's about a route. " +
                           "If the code is 'not_found', the middleware swallowed a matched endpoint.");
    }

    /// <summary>
    /// The regression an adversarial review caught before this shipped. The first attempt answered
    /// 404 from a middleware placed before <c>UseAuthorization</c>, keyed on
    /// <c>GetEndpoint() == null</c> — and Hangfire's dashboard is mounted by <c>app.Map</c>, classic
    /// branch middleware that never registers an endpoint. The middleware therefore swallowed
    /// <c>/hangfire</c> whole and <c>UseCodeSpaceHangfire</c> became dead code on the API role.
    ///
    /// <para>A fallback ENDPOINT cannot do that: routing reaches it only after every earlier branch
    /// has declined. This asserts the property that makes it safe, so a future move back to
    /// middleware fails here.</para>
    /// </summary>
    [Fact]
    public async Task The_not_found_answer_does_not_swallow_branch_mounted_paths()
    {
        await using var apiRole = new HangfireApiRoleFactory(_factory);

        var response = await apiRole.CreateClient().GetAsync("/hangfire");

        var body = response.Content.Headers.ContentType?.MediaType == "application/json"
            ? await response.Content.ReadFromJsonAsync<Dictionary<string, string>>()
            : null;

        body?.GetValueOrDefault("code").ShouldNotBe("not_found",
            customMessage: "/hangfire was answered by the catch-all rather than by Hangfire's own branch. The " +
                           "dashboard is mounted with app.Map and has no endpoint, so anything keyed on " +
                           "\"no endpoint matched\" unmounts it.");
    }

    /// <summary>Runs the same app with the Hangfire role that actually mounts the dashboard; the shared factory runs Worker, which mounts none.</summary>
    private sealed class HangfireApiRoleFactory : WebApplicationFactory<CodeSpace.Api.Program>
    {
        private readonly TaskLaunchApiFactory _inner;

        public HangfireApiRoleFactory(TaskLaunchApiFactory inner) { _inner = inner; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeSpaceStore:ConnectionString"] = _inner.ConnectionString,
                ["Authentication:Jwt:SymmetricKey"] = TaskLaunchApiFactory.JwtKey,
                ["OAuth:CallbackUrl"] = "http://localhost/api/credentials/oauth/callback",
                ["HangfireHosting"] = "Api",
            }));
        }
    }
}
