using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.ModelCredentials;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>The context-capacity management contract through real ASP.NET routing/auth/model binding/MediatR and real PostgreSQL.</summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ModelContextWindowEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public ModelContextWindowEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task The_exact_model_rows_capacity_can_be_set_read_and_cleared_over_http()
    {
        var world = await SeedAsync();

        var set = await SendAsync(world, HttpMethod.Put, $"/api/model-credentials/{world.CredentialId}/models/{world.ModelRowId}/context-window", new
        {
            modelCredentialId = Guid.NewGuid(),
            modelRowId = Guid.NewGuid(),
            contextWindowTokens = 131_072,
        });
        set.StatusCode.ShouldBe(HttpStatusCode.OK, $"PUT failed: {await set.Content.ReadAsStringAsync()}");

        var list = await SendAsync(world, HttpMethod.Get, $"/api/model-credentials/{world.CredentialId}/models");
        list.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET failed: {await list.Content.ReadAsStringAsync()}");
        (await list.Content.ReadFromJsonAsync<CredentialedModelSummary[]>()).ShouldHaveSingleItem().ContextWindowTokens.ShouldBe(131_072);

        var clear = await SendAsync(world, HttpMethod.Put, $"/api/model-credentials/{world.CredentialId}/models/{world.ModelRowId}/context-window", new { contextWindowTokens = (int?)null });
        clear.StatusCode.ShouldBe(HttpStatusCode.OK, $"clear failed: {await clear.Content.ReadAsStringAsync()}");

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().ModelCredentialModel.FindAsync(world.ModelRowId))!.ContextWindowTokens.ShouldBeNull();
    }

    private async Task<HttpResponseMessage> SendAsync(World world, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(world.UserId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", world.TeamId.ToString());
        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<World> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var modelRowId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"context-{suffix}@test.local", Name = "Context E2E", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"context-{suffix}", Name = "Context E2E", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ModelCredential.Add(new ModelCredential { Id = credentialId, TeamId = teamId, Provider = "Custom", DisplayName = "Opaque gateway", Status = CredentialStatus.Active });
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = modelRowId, ModelCredentialId = credentialId, ModelId = "house-alias", Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();

        return new World(userId, teamId, credentialId, modelRowId);
    }

    private sealed record World(Guid UserId, Guid TeamId, Guid CredentialId, Guid ModelRowId);
}
