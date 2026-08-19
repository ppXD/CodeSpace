using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// What an Agent Run log stream does when its team's STORAGE is the thing that is broken. The whole chain is real —
/// the real <see cref="IAgentRunLogService"/> over the real CAS runtime, the real driver broker, and a profile
/// revision whose credential reference dangles — so the classification under test is the one a deployment produces,
/// not one a fake asserts.
///
/// <para>The property this suite exists to protect: a permanent storage fault terminalizes the stream with a
/// storage-shaped cause instead of being retried until the capture budget expires.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunLogStorageFaultFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public AgentRunLogStorageFaultFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_unresolvable_storage_credential_terminalizes_the_stream_with_a_storage_cause_not_a_source_cause()
    {
        var world = await SeedWorldAsync();
        using var scope = _fixture.BeginScope();
        var logs = LogService(scope);
        var source = new StubLogSource(Enumerable.Repeat((byte)'c', 300 * 1024).ToArray());
        var bridge = new AgentRunLogCaptureBridge(logs, new StubStorageResolver(world.StorageProfileId), scope.Resolve<IAgentRunLogCaptureRecoveryService>(),
            NullLogger<AgentRunLogCaptureBridge>.Instance, new AgentRunLogCaptureBridgeOptions(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)));
        var expected = SandboxResultOf();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var session = await bridge.OpenAsync(OpenCapture(world, source), CancellationToken.None);
        var observed = await session.ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);

        observed.ShouldBeSameAs(expected, "shadow capture never reinterprets the harness result");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3),
            "a dangling credential is permanent; retrying it until the finalization budget cancels the capture leaves the stream Open for the reconciler to blame on the sandbox");
        var streams = await logs.ListMetadataAsync(world.TeamId, world.AgentRunId, CancellationToken.None);
        var stdout = streams.Single(value => value.StreamKind == AgentRunLogKinds.StandardOutput);
        stdout.State.ShouldBe(AgentRunLogStreamState.CaptureFailed);
        stdout.ErrorCode.ShouldBe("capture-storage-activation-failed",
            "the durable cause has to name the team's storage, or the operator is sent to look at the agent's log source");
    }

    private static AgentRunLogService LogService(ILifetimeScope scope) =>
        new(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), scope.Resolve<IArtifactCasRuntimeCoordinator>(), TimeProvider.System);

    private static AgentRunLogCaptureOpenRequest OpenCapture(World world, StubLogSource source) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, ActorId = world.ActorId, WorkerFenceEpoch = 7,
        Handle = new SandboxHandle { Kind = "stub", ProcessId = 1, SpoolDirectory = "/opaque", Deadline = DateTimeOffset.MaxValue, AgentRunLogCaptureSessionId = Guid.NewGuid() },
        Source = source, Redactor = SecretRedactor.None,
    };

    private static SandboxResult SandboxResultOf() => new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "legacy", Stderr = "legacy-error" };

    /// <summary>
    /// A team whose log storage profile is Active and whose revision exists, but whose credential reference names a
    /// credential that is not there — the cheapest faithful stand-in for the deployment fault this suite is about,
    /// and one the local-rwx driver would otherwise serve happily.
    /// </summary>
    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var root = NewRoot();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"agent-log-fault-{actorId:N}@test.local", Name = "Agent Log Fault" });
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-log-fault-{teamId:N}", Name = "Agent Log Fault", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"agent-log-fault-{profileId:N}", State = StorageProfileState.Active,
            CurrentRevision = 1, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = $"{{\"rootPath\":\"{root.Replace("\\", "\\\\")}\"}}",
            CredentialRef = $"db:{credentialId:D}:1", NamespaceFingerprint = $"sha256:{new string('a', 64)}",
            CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        var runId = Guid.NewGuid();
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Running, TaskJson = "{}",
            FenceEpoch = 7, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();
        return new World(teamId, actorId, profileId, credentialId, runId);
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-agent-log-fault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid StorageProfileId, Guid CredentialId, Guid AgentRunId);

    private sealed class StubStorageResolver(Guid profileId) : IAgentRunLogStorageResolver
    {
        public Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentRunLogStorageResolution>(new AgentRunLogStorageResolution.Ready(profileId, 1));
    }

    private sealed class StubLogSource(byte[] stdout) : ISandboxDurableLogSource
    {
        public IReadOnlyList<SandboxDurableLogDescriptor> DescribeLogs(SandboxHandle handle) =>
        [
            new("stdout", AgentRunLogKinds.StandardOutput, AgentRunLogRepresentations.PlainTextContentType, AgentRunLogRepresentations.Utf8ContentEncoding, "stub-spool/v1"),
        ];

        public Task<SandboxDurableLogReadResult> ReadAsync(SandboxDurableLogReadRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = stdout.LongLength - request.OffsetBytes;
            if (available == 0 && request.FinalDrain) return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.EndOfSource(false));
            if (available == 0 || (!request.FinalDrain && available < request.MinimumBytes)) return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.NoData());
            var length = (int)Math.Min(available, request.MaximumBytes);
            return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.Available(stdout.AsMemory((int)request.OffsetBytes, length)));
        }
    }
}
