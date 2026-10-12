using System.Security.Claims;
using Autofac;
using CodeSpace.Api.Extensions;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.Core.Settings.Authentication;
using CodeSpace.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CodeSpace.IntegrationTests.Authorization;

/// <summary>
/// The bearer handler as <see cref="AuthenticationExtension"/> configures it, reading the session cookie, against the
/// real session check in Postgres. Below the HTTP pipeline on purpose: this is the component every request passes
/// through, and each case isolates one rule of it — where the token comes from, which writes the cookie may not carry,
/// and that ending a session ends the cookie's too. The wire itself is <c>SessionCookieE2ETests</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SessionCookieAuthenticationTests
{
    private readonly PostgresFixture _fixture;

    public SessionCookieAuthenticationTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_cookie_alone_authenticates_a_live_account()
    {
        var account = await SeedAccountAsync();

        var result = await AuthenticateAsync(HttpMethods.Get, cookie: account.Token);

        result.Succeeded.ShouldBeTrue(result.Failure?.Message);
        SubjectOf(result).ShouldBe(account.Id);
    }

    [Fact]
    public async Task Ending_the_session_ends_the_cookie_too()
    {
        // The stamp is what a password change, a reset and a deactivation all rotate. A cookie is the same token,
        // so it must die at the same write — not when the browser gets round to expiring it.
        var account = await SeedAccountAsync();

        (await AuthenticateAsync(HttpMethods.Get, cookie: account.Token)).Succeeded.ShouldBeTrue("the control: the cookie is a live session before anything changes");

        await RotateStampAsync(account.Id);

        var result = await AuthenticateAsync(HttpMethods.Get, cookie: account.Token);

        result.Succeeded.ShouldBeFalse("the stamp the cookie's token was minted under is gone");
    }

    [Fact]
    public async Task The_header_is_read_first_and_the_cookie_never_rescues_it()
    {
        // A caller that chose a header gets the header's answer. Falling back to a cookie it happens to carry would let
        // a dead header quietly become a different, live identity.
        var cookieAccount = await SeedAccountAsync();
        var headerAccount = await SeedAccountAsync();

        await RotateStampAsync(headerAccount.Id);

        (await AuthenticateAsync(HttpMethods.Get, cookie: cookieAccount.Token)).Succeeded.ShouldBeTrue("the control: the cookie alone is a live session");

        var result = await AuthenticateAsync(HttpMethods.Get, cookie: cookieAccount.Token, bearer: headerAccount.Token);

        result.Succeeded.ShouldBeFalse("the header's token is dead, and the live cookie beside it must not stand in");
    }

    [Fact]
    public async Task A_live_header_decides_who_the_caller_is_whatever_cookie_rides_along()
    {
        var cookieAccount = await SeedAccountAsync();
        var headerAccount = await SeedAccountAsync();

        var result = await AuthenticateAsync(HttpMethods.Get, cookie: cookieAccount.Token, bearer: headerAccount.Token);

        result.Succeeded.ShouldBeTrue(result.Failure?.Message);
        SubjectOf(result).ShouldBe(headerAccount.Id);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task A_write_riding_the_cookie_without_the_csrf_header_is_not_authenticated(string method)
    {
        var account = await SeedAccountAsync();

        var refused = await AuthenticateAsync(method, cookie: account.Token);
        var guarded = await AuthenticateAsync(method, cookie: account.Token, csrfHeader: true);

        refused.Succeeded.ShouldBeFalse($"a {method} another site's form could have sent");
        refused.Failure!.Message.ShouldContain(SessionCookie.CsrfHeader, Case.Sensitive, "the log line must say what was missing");
        guarded.Succeeded.ShouldBeTrue($"the control: the same cookie with the header is a live session — {guarded.Failure?.Message}");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task A_bearer_write_needs_no_csrf_header(string method)
    {
        // API clients, the CLI and MCP never sent it and must not have to: a header credential is not something a
        // browser attaches on another site's behalf.
        var account = await SeedAccountAsync();

        var result = await AuthenticateAsync(method, bearer: account.Token);

        result.Succeeded.ShouldBeTrue(result.Failure?.Message);
    }

    [Theory]
    [InlineData(true, false, false, true)]    // the browser on its cookie: an endpoint that mints a session must keep it out of the body
    [InlineData(true, true, false, false)]    // a header client with a stray cookie
    [InlineData(false, true, false, false)]   // a plain header client
    [InlineData(true, false, true, false)]    // a cookie whose session has ended authenticates nobody, so it carried nothing
    public async Task Only_a_principal_the_cookie_established_counts_as_carried_by_it(bool sendCookie, bool sendBearer, bool endSession, bool expected)
    {
        // The handler as configured decides who the caller is; SessionCookie.Authenticated reads that decision, as the
        // session-minting endpoints do after the middleware ran.
        var account = await SeedAccountAsync();

        if (endSession) await RotateStampAsync(account.Id);

        var context = await AuthenticatedContextAsync(HttpMethods.Post, cookie: sendCookie ? account.Token : null, bearer: sendBearer ? account.Token : null, csrfHeader: true);

        SessionCookie.Authenticated(context).ShouldBe(expected);
    }

    private async Task<AuthenticateResult> AuthenticateAsync(string method, string? cookie = null, string? bearer = null, bool csrfHeader = false)
    {
        using var fixtureScope = _fixture.BeginScope();
        await using var provider = BuildAuthentication(fixtureScope);
        await using var requestScope = provider.CreateAsyncScope();

        return await RequestFor(requestScope.ServiceProvider, method, cookie, bearer, csrfHeader).AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
    }

    /// <summary>A request after the authentication middleware: <see cref="HttpContext.User"/> is whatever the handler established, as an action sees it.</summary>
    private async Task<HttpContext> AuthenticatedContextAsync(string method, string? cookie, string? bearer, bool csrfHeader)
    {
        using var fixtureScope = _fixture.BeginScope();
        await using var provider = BuildAuthentication(fixtureScope);
        await using var requestScope = provider.CreateAsyncScope();

        var context = RequestFor(requestScope.ServiceProvider, method, cookie, bearer, csrfHeader);
        var result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);

        if (result.Succeeded) context.User = result.Principal;

        return context;
    }

    private static DefaultHttpContext RequestFor(IServiceProvider services, string method, string? cookie, string? bearer, bool csrfHeader)
    {
        var context = new DefaultHttpContext { RequestServices = services };

        context.Request.Method = method;

        if (cookie != null) context.Request.Headers.Cookie = $"{SessionCookie.Name}={cookie}";
        if (bearer != null) context.Request.Headers.Authorization = $"Bearer {bearer}";
        if (csrfHeader) context.Request.Headers[SessionCookie.CsrfHeader] = "1";

        return context;
    }

    /// <summary>The production authentication registration, with the session check resolved from the fixture's real container and database.</summary>
    private static ServiceProvider BuildAuthentication(ILifetimeScope fixtureScope)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Jwt:SymmetricKey"] = fixtureScope.Resolve<JwtSymmetricKeySetting>().Value }).Build();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddCustomAuthentication(configuration, new ProductionEnvironment());
        services.AddScoped(_ => fixtureScope.Resolve<ISessionValidator>());

        return services.BuildServiceProvider();
    }

    private async Task<(Guid Id, string Token)> SeedAccountAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"cookie-{Guid.NewGuid():N}@session.test", Name = $"cookie-{Guid.NewGuid():N}", SecurityStamp = Guid.NewGuid() };

        db.User.Add(user);
        await db.SaveChangesAsync();

        return (user.Id, scope.Resolve<IJwtTokenIssuer>().Issue(user).Token);
    }

    private async Task RotateStampAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().User.Where(u => u.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.SecurityStamp, Guid.NewGuid()));
    }

    private static Guid SubjectOf(AuthenticateResult result) => Guid.Parse(result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
