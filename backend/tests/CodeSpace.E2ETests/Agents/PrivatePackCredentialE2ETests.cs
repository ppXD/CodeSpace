using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>
/// A private agent pack imported by pasting a git URL that embeds a token, through the whole HTTP surface.
///
/// <para>Tier: 🟢 High-fidelity (Rule 12) — the real ASP.NET pipeline (routing, JWT auth, the <c>X-Team-Id</c> scope,
/// model binding, the controllers, the exception filter, the mediator and its authorization behaviors), the production
/// <see cref="PackCloneFetcher"/> on the real local runner and real <c>git</c>, and real Postgres, against a loopback
/// smart-HTTP remote that answers 401 to any request without the fake token. One documented seam: the pack-host
/// allowlist accepts that loopback http remote (production admits https hosts only). The backfill job is resolved from
/// a host DI scope with no HTTP context, as a worker's job scope resolves it (its Hangfire registration is covered by
/// <c>RecurringJobWorkerSmokeE2ETests</c>).</para>
///
/// <para>Must hold: no response a Viewer can request carries the token, nor does the stored row, nor the error body of a
/// Sync or an add whose clone from the sealed source fails; a Member can still sync the pack and add what the sync
/// discovered; a legacy row that stored the token verbatim is sealed by the job and
/// keeps syncing. Each test owns its remote (GUID-suffixed temp root, a loopback port allocated by the server) and
/// removes it on every path. Only the token-carrying URL is ever cloned here, so git never asks a credential helper
/// (the tokened clone resets them) — the refused-without-token check is an HTTP probe, not a clone.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class PrivatePackCredentialE2ETests : IClassFixture<PrivatePackCredentialE2ETests.PrivatePackApiFactory>
{
    private const string Token = "fake-e2e-pack-token-0123456789";
    private const string Agent = "agents/reviewer.md";
    private const string NewSkill = "skills/new-skill/SKILL.md";

    private readonly PrivatePackApiFactory _factory;

    public PrivatePackCredentialE2ETests(PrivatePackApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_pasted_token_is_never_returned_and_the_private_pack_keeps_syncing_over_http()
    {
        if (!await GitReadyAsync()) return;

        using var remote = await PrivateRemote.StartAsync();
        var world = await SeedAsync();

        (await remote.AnonymousProbeAsync()).ShouldBe(HttpStatusCode.Unauthorized, "fixture check: the remote refuses a read that presents no token");

        var import = await SendAsync(world.MemberId, world.TeamId, HttpMethod.Post, "/api/agents/import-url", new { url = remote.TokenedUrl, sourcePaths = new[] { Agent } });
        var importBody = await ShouldSucceedAsync(import, "import-url");
        var packId = JsonDocument.Parse(importBody).RootElement.GetProperty("packId").GetGuid();

        foreach (var path in new[] { "/api/packs", $"/api/packs/{packId}" })
        {
            var body = await ShouldSucceedAsync(await SendAsync(world.ViewerId, world.TeamId, HttpMethod.Get, path), $"a Viewer's GET {path}");
            body.ShouldContain(remote.CleanUrl, Case.Sensitive, $"GET {path} still shows the pack's source, without its userinfo");
        }

        await remote.PublishUpstreamChangeAsync();

        var syncBody = await ShouldSucceedAsync(await SendAsync(world.MemberId, world.TeamId, HttpMethod.Post, $"/api/packs/{packId}/sync"), "sync");
        var sync = JsonDocument.Parse(syncBody).RootElement;
        sync.GetProperty("updated").GetInt32().ShouldBe(1, "the sealed token cloned the private remote and the changed agent was refreshed");
        sync.GetProperty("newArtifacts").GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("sourcePath").GetString()).ShouldContain(NewSkill);

        var addBody = await ShouldSucceedAsync(await SendAsync(world.MemberId, world.TeamId, HttpMethod.Post, $"/api/packs/{packId}/import", new { sourcePaths = new[] { NewSkill } }), "import from the pack");
        JsonDocument.Parse(addBody).RootElement.GetProperty("packId").GetGuid().ShouldBe(packId, "the discovered artifact is added to the pack it came from");

        var detail = await ShouldSucceedAsync(await SendAsync(world.ViewerId, world.TeamId, HttpMethod.Get, $"/api/packs/{packId}"), "a Viewer's GET of the pack after the add");
        JsonDocument.Parse(detail).RootElement.GetProperty("artifacts").EnumerateArray().Select(a => a.GetProperty("sourcePath").GetString()).ShouldContain(NewSkill);

        (await SendAsync(world.ViewerId, world.TeamId, HttpMethod.Post, $"/api/packs/{packId}/sync")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a Viewer cannot make the server spend the pack's token");

        ShouldHoldNoToken(await RawRowAsync(packId), "the stored pack row");
    }

    [Fact]
    public async Task A_failed_clone_from_the_sealed_source_names_no_token_in_the_http_error_body()
    {
        if (!await GitReadyAsync()) return;

        using var remote = await PrivateRemote.StartAsync();
        var world = await SeedAsync();

        var import = await SendAsync(world.MemberId, world.TeamId, HttpMethod.Post, "/api/agents/import-url", new { url = remote.TokenedUrl, sourcePaths = new[] { Agent } });
        var packId = JsonDocument.Parse(await ShouldSucceedAsync(import, "import-url")).RootElement.GetProperty("packId").GetGuid();

        // The token stays valid; the saved ref is gone upstream, so each clone authenticates with the decrypted source and then fails.
        await ExecuteAsync("UPDATE pack SET reference = 'deleted-branch' WHERE id = @id", packId);

        foreach (var (path, body) in new (string, object?)[] { ($"/api/packs/{packId}/sync", null), ($"/api/packs/{packId}/import", new { sourcePaths = new[] { NewSkill } }) })
        {
            var response = await SendAsync(world.MemberId, world.TeamId, HttpMethod.Post, path, body);
            var text = await response.Content.ReadAsStringAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"POST {path}: {text}");
            text.ShouldContain("deleted-branch", Case.Sensitive, $"fixture check: POST {path} reports git's reason, so the scan below reads the clone failure");
            ShouldHoldNoToken(text, $"the POST {path} error body");
        }
    }

    [Fact]
    public async Task A_legacy_row_holding_a_token_is_sealed_by_the_backfill_job_and_keeps_syncing_over_http()
    {
        if (!await GitReadyAsync()) return;

        using var remote = await PrivateRemote.StartAsync();
        var world = await SeedAsync();
        var packId = await SeedLegacyPackAsync(world, remote.TokenedUrl);

        ShouldHoldNoToken(await ShouldSucceedAsync(await SendAsync(world.ViewerId, world.TeamId, HttpMethod.Get, $"/api/packs/{packId}"), "a Viewer's GET of the unsealed legacy pack"), "the read model, before the backfill reaches the row");
        (await RawRowAsync(packId)).ShouldContain(Token, Case.Sensitive, "positive control: the legacy row holds the token at rest");

        await RunBackfillJobUntilSealedAsync(packId);

        var row = await RawRowAsync(packId);
        ShouldHoldNoToken(row, "the sealed row");
        JsonDocument.Parse(row).RootElement.GetProperty("encrypted_clone_url").GetString().ShouldNotBeNullOrEmpty("the job sealed the token rather than dropping it");

        var syncBody = await ShouldSucceedAsync(await SendAsync(world.MemberId, world.TeamId, HttpMethod.Post, $"/api/packs/{packId}/sync"), "sync of the sealed legacy pack");
        JsonDocument.Parse(syncBody).RootElement.GetProperty("newArtifacts").GetProperty("agents").EnumerateArray().Select(a => a.GetProperty("sourcePath").GetString()).ShouldContain(Agent, "the sealed token cloned the private remote");
    }

    /// <summary>The production job, resolved from a host scope with no HTTP context — the shape a worker's job scope has — until the owned row is sealed. The sweep is deployment-wide; this waits on the row the test owns.</summary>
    private async Task RunBackfillJobUntilSealedAsync(Guid packId)
    {
        const int maxTicks = 5;

        for (var tick = 0; tick < maxTicks; tick++)
        {
            using (var scope = _factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<PackCloneUrlBackfillRecurringJob>().Execute();

            if (!(await RawRowAsync(packId)).Contains(Token, StringComparison.Ordinal)) return;
        }

        throw new ShouldAssertException($"pack {packId} still holds the token after {maxTicks} ticks of {nameof(PackCloneUrlBackfillRecurringJob)} — check the host log for 'Pack clone-URL backfill failed for pack {packId}' and run `SELECT url, encrypted_clone_url FROM pack WHERE id = '{packId}'` against the fixture database");
    }

    private async Task ExecuteAsync(string sql, Guid packId)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", packId);

        (await command.ExecuteNonQueryAsync()).ShouldBe(1, $"fixture check: '{sql}' updated pack {packId}");
    }

    private async Task<string> RawRowAsync(Guid packId)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT row_to_json(p)::text FROM pack p WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", packId);

        return (string)(await command.ExecuteScalarAsync()).ShouldNotBeNull($"pack {packId} must exist");
    }

    /// <summary>A pack row exactly as the code before the seal wrote one: the pasted URL verbatim, none of the new columns.</summary>
    private async Task<Guid> SeedLegacyPackAsync(World world, string url)
    {
        var id = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow.AddYears(-30);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO pack (id, team_id, kind, name, url, created_date, created_by, last_modified_date, last_modified_by) VALUES (@id, @team, 'GitUrl', 'remote', @url, @created, @user, @created, @user)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("team", world.TeamId);
        command.Parameters.AddWithValue("url", url);
        command.Parameters.AddWithValue("created", created);
        command.Parameters.AddWithValue("user", world.MemberId);
        await command.ExecuteNonQueryAsync();

        return id;
    }

    private async Task<string> ShouldSucceedAsync(HttpResponseMessage response, string what)
    {
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{what} failed: {body}");
        ShouldHoldNoToken(body, $"the {what} response");
        return body;
    }

    private static void ShouldHoldNoToken(string text, string what) => text.ShouldNotContain(Token, Case.Insensitive, $"{what} must not hold the pasted token");

    private async Task<HttpResponseMessage> SendAsync(Guid userId, Guid teamId, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());
        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<World> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var world = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var suffix = Guid.NewGuid().ToString("N")[..8];

        db.Team.Add(new Team { Id = world.TeamId, Slug = $"private-pack-{suffix}", Name = "Private Pack E2E", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        foreach (var (userId, role) in new[] { (world.MemberId, TeamRole.Member), (world.ViewerId, TeamRole.Viewer) })
        {
            db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"private-pack-{userId:N}@test.local", Name = $"Private Pack {role}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = world.TeamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        }

        await db.SaveChangesAsync();
        return world;
    }

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;

        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private sealed record World(Guid TeamId, Guid MemberId, Guid ViewerId);

    /// <summary>The app with one seam: the pack-host allowlist also admits the loopback http test remote.</summary>
    public sealed class PrivatePackApiFactory : TaskLaunchApiFactory
    {
        protected override void ConfigureContractTestServices(ContainerBuilder builder) =>
            builder.RegisterType<LoopbackRemoteAllowlist>().As<IPackHostAllowlist>().SingleInstance();
    }

    /// <summary>Admits only the loopback http remote these tests start — every other URL is refused, as the production allowlist would refuse an http one.</summary>
    private sealed class LoopbackRemoteAllowlist : IPackHostAllowlist
    {
        public bool IsAllowed(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1";

        public void EnsureAllowed(string url)
        {
            if (!IsAllowed(url)) throw new PackImportException("Only the loopback test remote is an allowed pack source in this fixture.");
        }
    }

    /// <summary>A bare repository holding a pack (an agent and a skill), served over smart HTTP behind the fake token. GUID-suffixed temp root; disposal stops the server and removes the root.</summary>
    private sealed class PrivateRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-private-pack-" + Guid.NewGuid().ToString("N"));
        private readonly GitTestRemoteServer _server;

        private PrivateRemote()
        {
            Directory.CreateDirectory(_root);
            _server = new GitTestRemoteServer(_root, requiredBasicCredential: $"x-access-token:{Token}");
        }

        public string CleanUrl => _server.Url;
        public string TokenedUrl => _server.Url.Replace("http://", $"http://x-access-token:{Token}@", StringComparison.Ordinal);

        private string Bare => Path.Combine(_root, "remote.git");
        private string Seed => Path.Combine(_root, "seed");

        public static async Task<PrivateRemote> StartAsync()
        {
            var remote = new PrivateRemote();

            try
            {
                await Git(remote._root, "init", "--bare", "-b", "main", remote.Bare);
                await Git(remote._root, "init", "-b", "main", remote.Seed);
                await remote.CommitAndPushAsync("pack", new Dictionary<string, string>
                {
                    [Agent] = "---\nname: reviewer\ndescription: Reviews PRs.\n---\nYou review v1.",
                    ["skills/tdd/SKILL.md"] = "---\nname: tdd\ndescription: Use when implementing.\n---\n# TDD v1",
                });
                return remote;
            }
            catch
            {
                remote.Dispose();
                throw;
            }
        }

        public Task PublishUpstreamChangeAsync() => CommitAndPushAsync("upstream change", new Dictionary<string, string>
        {
            [Agent] = "---\nname: reviewer\ndescription: Reviews PRs.\n---\nYou review v2 now.",
            [NewSkill] = "---\nname: new-skill\ndescription: Brand new.\n---\n# New",
        });

        /// <summary>The smart-HTTP ref advertisement a clone starts with, asked for without credentials.</summary>
        public async Task<HttpStatusCode> AnonymousProbeAsync()
        {
            using var http = new HttpClient();
            return (await http.GetAsync($"{CleanUrl}/info/refs?service=git-upload-pack")).StatusCode;
        }

        private async Task CommitAndPushAsync(string message, IReadOnlyDictionary<string, string> files)
        {
            foreach (var (path, content) in files)
            {
                var full = Path.Combine(Seed, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content);
            }

            await Git(Seed, "add", "-A");
            await Git(Seed, "-c", "user.email=test@codespace.dev", "-c", "user.name=Test", "-c", "commit.gpgsign=false", "commit", "-m", message);
            await Git(Seed, "push", Bare, "main");
        }

        private static Task<string> Git(string workdir, params string[] args) => GitTestRemoteServer.RunFixtureGitAsync(workdir, args);

        public void Dispose()
        {
            _server.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
