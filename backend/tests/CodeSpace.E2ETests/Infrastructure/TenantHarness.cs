using System.Net.Http.Headers;
using System.Net.Http.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// Accounts, teams and signed requests for HTTP tests that cross a tenancy boundary — two teams, a
/// role inside one of them, and a request made as one specific account under one specific team header.
///
/// <para>Every account gets a unique address and name, because both are sign-in identifiers now and a
/// shared fixture name would make one test's account resolve as another's.</para>
/// </summary>
public sealed class TenantHarness
{
    private readonly WebApplicationFactory<CodeSpace.Api.Program> _factory;

    public TenantHarness(WebApplicationFactory<CodeSpace.Api.Program> factory) { _factory = factory; }

    public async Task<Guid> AccountAsync(string? email = null, string? name = null, string? password = null, Guid? id = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var hash = password == null ? null : scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(password);
        var user = new User { Id = id ?? Guid.NewGuid(), SecurityStamp = TestToken.SeedStamp, Email = email ?? $"account-{suffix}@tenant.test", Name = name ?? $"Account {suffix}", PasswordHash = hash, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.User.Add(user);
        await db.SaveChangesAsync();

        return user.Id;
    }

    public async Task<Guid> TeamAsync(params (Guid UserId, TeamRole Role)[] members)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var team = new Team { Id = Guid.NewGuid(), Slug = $"tenant-{suffix}", Name = $"Team {suffix}", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.Team.Add(team);

        foreach (var (userId, role) in members)
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();

        return team.Id;
    }

    public async Task AddMemberAsync(Guid teamId, Guid userId, TeamRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
    }

    /// <summary>An account holding the instance <c>Admin</c> role — the one principal that bypasses team scope.</summary>
    public async Task MakeGlobalAdminAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var roleId = await db.Role.AsNoTracking().Where(r => r.Name == Roles.Admin).Select(r => r.Id).SingleAsync();

        db.RoleUser.Add(new RoleUser { Id = Guid.NewGuid(), RoleId = roleId, UserId = userId });
        await db.SaveChangesAsync();
    }

    public async Task<T> DbAsync<T>(Func<CodeSpaceDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();

        return await work(scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>());
    }

    public HttpRequestMessage Request(HttpMethod method, string url, Guid? userId, Guid? teamId, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);

        if (body != null) request.Content = JsonContent.Create(body);
        if (userId is { } id) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(id, TestToken.SeedStamp));
        if (teamId is { } team) request.Headers.Add("X-Team-Id", team.ToString());

        return request;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid? userId, Guid? teamId, object? body = null) =>
        await Client().SendAsync(Request(method, url, userId, teamId, body));

    /// <summary>
    /// No redirects followed and no cookie jar: a test that cares about a redirect or a cookie reads and sends it itself.
    /// Plain <c>http://localhost</c> unless a test names another origin — the scheme reaches the app as the request's own.
    /// </summary>
    public HttpClient Client(Uri? baseAddress = null) => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = baseAddress ?? new Uri("http://localhost") });

    public static async Task<string> DescribeAsync(HttpResponseMessage response) => $"got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
}
