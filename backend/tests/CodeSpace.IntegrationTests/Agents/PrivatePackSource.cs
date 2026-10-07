using System.Collections.Concurrent;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Identity;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// A pack source for the pack-credential flows: the loopback smart-HTTP remote (<see cref="GitPublishRemoteFixture"/>)
/// seeded with a pack — an agent and a skill — that, when <see cref="StartAsync"/> is asked for a PRIVATE source,
/// refuses every request without the fake token, so a clone that succeeds proves the token authenticated it. The clone
/// is the production <see cref="PackCloneFetcher"/> on the real <see cref="LocalProcessRunner"/>, run under a scratch
/// HOME whose global config is its own and with system config off: no clone here — the refused, token-less ones
/// included — reads the real global config or asks the real keychain. The allowlist accepts the loopback http remote,
/// the one seam (the same <c>AllowAll</c> the sibling pack flows use).
/// </summary>
internal sealed class PrivatePackSource : IAsyncDisposable
{
    public const string Token = GitPublishRemoteFixture.FakeToken;

    public const string Agent = "agents/reviewer.md";
    public const string Skill = "skills/tdd/SKILL.md";
    public const string NewSkill = "skills/new-skill/SKILL.md";

    private static readonly IReadOnlyDictionary<string, string> InitialFiles = new Dictionary<string, string>
    {
        [Agent] = "---\nname: reviewer\ndescription: Reviews PRs.\n---\nYou review v1.",
        [Skill] = "---\nname: tdd\ndescription: Use when implementing.\n---\n# TDD v1",
    };

    private static readonly IReadOnlyDictionary<string, string> UpstreamChange = new Dictionary<string, string>
    {
        [Agent] = "---\nname: reviewer\ndescription: Reviews PRs.\n---\nYou review v2 now.",
        [NewSkill] = "---\nname: new-skill\ndescription: Brand new.\n---\n# New",
    };

    private readonly string _home = Directory.CreateTempSubdirectory("cs-packcred-home-").FullName;

    private PrivatePackSource(bool authenticateReads)
    {
        Remote = new GitPublishRemoteFixture { AuthenticateReads = authenticateReads };

        var environment = new Dictionary<string, string> { ["HOME"] = _home, ["GIT_CONFIG_GLOBAL"] = Path.Combine(_home, ".gitconfig"), ["GIT_CONFIG_NOSYSTEM"] = "1" };
        Fetcher = new PackCloneFetcher(new AllowAll(), new SandboxRunnerRegistry(new ISandboxRunner[] { new ScratchHostRunner(environment) }), NullLogger<PackCloneFetcher>.Instance);
    }

    public GitPublishRemoteFixture Remote { get; }

    /// <summary>The production fetcher every flow in these tests clones through.</summary>
    public IPackSourceFetcher Fetcher { get; }

    public string CleanUrl => Remote.Url;

    public string TokenedUrl => UrlWith($"x-access-token:{Token}");

    /// <summary>The remote's URL with <paramref name="userInfo"/> pasted into it, the way an operator would.</summary>
    public string UrlWith(string userInfo) => Remote.Url.Replace("http://", $"http://{userInfo}@", StringComparison.Ordinal);

    public static async Task<PrivatePackSource> StartAsync(bool authenticateReads = true)
    {
        var source = new PrivatePackSource(authenticateReads);

        try
        {
            await source.Remote.StartAsync();
            await source.Remote.CommitFilesAsync("pack", InitialFiles);
            return source;
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    /// <summary>Change the agent and add a skill upstream — what a Sync must then see.</summary>
    public Task PublishUpstreamChangeAsync() => Remote.CommitFilesAsync("upstream change", UpstreamChange);

    public static async Task<bool> GitAvailableAsync()
    {
        if (OperatingSystem.IsWindows()) return false;

        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>Every spelling of <see cref="Token"/> a leak could take — pasted, and percent-encoded the way the rotation test pastes it.</summary>
    public static IEnumerable<string> TokenSpellings() => new[] { Token, Token.Replace("-", "%2D", StringComparison.Ordinal), Token.Replace("-", "%2d", StringComparison.Ordinal) };

    public static void ShouldHoldNoToken(string text, string what)
    {
        foreach (var spelling in TokenSpellings()) text.ShouldNotContain(spelling, Case.Insensitive, $"{what} must not hold the token ('{spelling}')");
    }

    public async ValueTask DisposeAsync()
    {
        await Remote.DisposeAsync();
        try { Directory.Delete(_home, recursive: true); } catch { /* best-effort */ }
    }

    private sealed class AllowAll : IPackHostAllowlist
    {
        public bool IsAllowed(string url) => true;
        public void EnsureAllowed(string url) { }
    }

    /// <summary>The real local runner, with the scratch HOME and global config layered over every command's environment.</summary>
    private sealed class ScratchHostRunner(IReadOnlyDictionary<string, string> environment) : ISandboxRunner
    {
        private readonly LocalProcessRunner _inner = new();

        public string Kind => LocalProcessRunner.LocalKind;

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            var env = new Dictionary<string, string>(spec.Environment);
            foreach (var (key, value) in environment) env[key] = value;

            return _inner.RunAsync(spec with { Environment = env }, cancellationToken);
        }
    }
}

/// <summary>A team with an Owner (who imports and syncs) and a Viewer (who may only read).</summary>
internal sealed record PackCredentialTeam(Guid TeamId, Guid OwnerId, Guid ViewerId);

/// <summary>The pack columns the credential flows assert on, read back without tracking.</summary>
internal sealed record PackColumns(string? Url, string? EncryptedCloneUrl, Guid? DuplicateOfPackId, DateTimeOffset? DeletedDate);

/// <summary>
/// The real mediator, the real Postgres and the real production protector, as the pack-credential flows drive them:
/// requests are sent as a NON-Admin team member (Admin bypasses tenancy, so a Viewer's read is a real membership check),
/// and rows are read back both through EF and as raw SQL — the row exactly as stored, every column included. Without a
/// <see cref="PrivatePackSource"/> it serves the flows that never clone.
/// </summary>
internal sealed class PackCredentialHarness(PostgresFixture fixture, PrivatePackSource? source = null)
{
    public async Task<PackCredentialTeam> SeedTeamAsync()
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var team = new PackCredentialTeam(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        foreach (var (userId, role) in new[] { (team.OwnerId, TeamRole.Owner), (team.ViewerId, TeamRole.Viewer) })
        {
            db.User.Add(new User { Id = userId, Email = $"packcred-{userId:N}@test.local", Name = $"packcred-{userId:N}" });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.TeamId, UserId = userId, Role = role });
        }

        db.Team.Add(new Team { Id = team.TeamId, Slug = $"packcred-{team.TeamId:N}", Name = "Pack Credential Team", Kind = TeamKind.Workspace });

        await db.SaveChangesAsync();
        return team;
    }

    /// <summary>Send <paramref name="request"/> as <paramref name="userId"/> in <paramref name="teamId"/>; with <paramref name="log"/>, every line the pipeline logs for it lands there too.</summary>
    public async Task<T> SendAsync<T>(Guid userId, Guid teamId, IRequest<T> request, CapturedLog? log = null)
    {
        using var scope = fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "pack-credential")).As<ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<ICurrentTeam>().SingleInstance();
            if (source is not null) b.RegisterInstance(source.Fetcher).As<IPackSourceFetcher>().SingleInstance();
            if (log is not null) log.Register(b);
        });

        return await scope.Resolve<IMediator>().Send(request);
    }

    /// <summary>Import <paramref name="sourcePaths"/> from <paramref name="url"/> as the Owner; every path must land.</summary>
    public async Task<Guid> ImportAsync(PackCredentialTeam team, string url, params string[] sourcePaths)
    {
        var result = await SendAsync(team.OwnerId, team.TeamId, new ImportPackFromUrlCommand { Url = url, SourcePaths = sourcePaths });

        result.Items.ShouldAllBe(i => i.Outcome == PackImportOutcome.Imported || i.Outcome == PackImportOutcome.Updated, "fixture check: the import landed every selected artifact");
        return result.PackId;
    }

    /// <summary>One backfill pass through the real mediator, in its own scope — as a worker tick runs it.</summary>
    public async Task BackfillPassAsync()
    {
        using var scope = fixture.BeginScope();
        await scope.Resolve<IMediator>().Send(new BackfillPackCloneUrlsCommand());
    }

    /// <summary>Run backfill passes until none of <paramref name="packIds"/> still carries a credential in its URL. The sweep is deployment-wide, so this waits on the rows the test owns, never on the pass's tally.</summary>
    public async Task BackfillUntilSealedAsync(params Guid[] packIds)
    {
        const int maxPasses = 5;

        for (var pass = 0; pass < maxPasses; pass++)
        {
            await BackfillPassAsync();

            var urls = await Task.WhenAll(packIds.Select(async id => (await ColumnsAsync(id)).Url!));
            if (!urls.Any(PackCloneUrlProtector.CarriesCredential)) return;
        }

        throw new ShouldAssertException($"packs {string.Join(", ", packIds)} still carry a credential in pack.url after {maxPasses} backfill passes — check the candidate filter in PackCloneUrlBackfillService.LoadCandidatesAsync and the warnings it logged (a pack it could not seal stays a candidate)");
    }

    public async Task<PackColumns> ColumnsAsync(Guid packId)
    {
        using var scope = fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Pack.AsNoTracking().Where(p => p.Id == packId).Select(p => new PackColumns(p.Url, p.EncryptedCloneUrl, p.DuplicateOfPackId, p.DeletedDate)).SingleAsync();
    }

    /// <summary>The URL the production protector would clone the pack from.</summary>
    public async Task<string> CloneUrlOfAsync(Guid packId)
    {
        using var scope = fixture.BeginScope();
        var pack = await scope.Resolve<CodeSpaceDbContext>().Pack.AsNoTracking().SingleAsync(p => p.Id == packId);

        return scope.Resolve<IPackCloneUrlProtector>().CloneUrlOf(pack);
    }

    /// <summary>The row exactly as Postgres stores it, every column included — what a database dump or a backup would hold.</summary>
    public async Task<string> RawRowAsync(Guid packId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT row_to_json(p)::text FROM pack p WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", packId);

        return (string)(await command.ExecuteScalarAsync()).ShouldNotBeNull($"pack {packId} must exist");
    }

    /// <summary>Insert a pack row exactly as the code before the seal wrote one: the pasted URL verbatim, none of the new columns.</summary>
    public async Task<Guid> SeedLegacyPackAsync(PackCredentialTeam team, string url, DateTimeOffset createdDate, bool deleted = false)
    {
        var id = Guid.NewGuid();

        await ExecuteAsync(
            "INSERT INTO pack (id, team_id, kind, name, url, created_date, created_by, last_modified_date, last_modified_by, deleted_date) VALUES (@id, @team, 'GitUrl', 'remote', @url, @created, @user, @created, @user, @deleted)",
            ("id", id), ("team", team.TeamId), ("url", url), ("created", createdDate), ("user", team.OwnerId), ("deleted", deleted ? (object)createdDate : DBNull.Value));

        return id;
    }

    /// <summary>Turn an imported pack back into the shape a legacy import left: the pasted URL in <c>url</c>, nothing sealed.</summary>
    public Task MakeLegacyAsync(Guid packId, string url) =>
        ExecuteAsync("UPDATE pack SET url = @url, encrypted_clone_url = NULL, duplicate_of_pack_id = NULL WHERE id = @id", ("url", url), ("id", packId));

    public Task ClearSealedSourceAsync(Guid packId) => ExecuteAsync("UPDATE pack SET encrypted_clone_url = NULL WHERE id = @id", ("id", packId));

    public async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

        return await command.ExecuteNonQueryAsync();
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

/// <summary>
/// Every line the mediator pipeline logs inside one request scope — the logging behavior, the transaction behavior and the
/// failure observer, each with its exception rendered as a sink renders it — so a test can scan what an operator's log would
/// hold. It replaces the scope's <see cref="ILoggerFactory"/> and <see cref="ILogger{T}"/>, which those scoped behaviors resolve.
/// </summary>
internal sealed class CapturedLog : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    public string Text => string.Join('\n', _lines);

    public void Register(ContainerBuilder builder)
    {
        builder.RegisterInstance(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(this))).As<ILoggerFactory>();
        builder.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>));
    }

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");

    public void Dispose() { }
}
