using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Authority;
using CodeSpace.Core.Services.Agents.Credentials;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Recovery;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.IntegrationTests.Agents.Infrastructure;

/// <summary>
/// A scripted agent CLI that keeps a resumable session the way the real ones do: a JSONL file in its config home, at
/// the path the PRODUCTION <see cref="ClaudeCodeHarness"/> locates (never restated here), restored by the runner from
/// <see cref="SandboxSpec.ConfigHomeFiles"/> and read back on a continuation. Only the model is scripted; the executor,
/// the durable runner, the config-home materialization, the capture, the checkpointer and the artifact store are the
/// production ones.
///
/// <para>A fresh run writes into its session what a real agent writes there when it runs <c>printenv</c> as a tool
/// call and restates its goal: the model credential the harness injected (<see cref="KeyEnvVar"/>), and the goal itself —
/// which carries any workflow secret the task embeds. Its stdout names the session and nothing secret.</para>
///
/// <para>The credential is whatever the launch handed it: the provider key on the direct path, or — when the executor
/// was given a broker (<see cref="ExecuteAsync"/>) — the lease's run token in the key's place, as a real harness's
/// brokered projection carries it. A deployment that requires confinement refuses the direct path outright.</para>
///
/// <para>Given a release file, a fresh run writes its session and then holds — naming nothing on stdout — until that
/// file appears: the shape of an agent whose launching worker goes away before the agent says anything, so everything
/// about its session is the re-attaching worker's to see.</para>
///
/// <para>A continuation (<see cref="AgentTask.RestoredTranscript"/> set) prints every line of the session file it was
/// restored and then one line of its own. What it printed is what it READ: the restored file as the runner wrote it,
/// so a test parsing those lines is parsing exactly what a resuming CLI parses.</para>
/// </summary>
public sealed class SessionKeepingCli : IAgentHarness, IAgentSessionTranscript, IModelCredentialProjector, IBrokeredModelCredentialProjector
{
    public const string HarnessKind = "scripted-session";

    /// <summary>The env var this harness projects the model credential onto — the key, or a brokered run's token — and the one the agent echoes into its session.</summary>
    public const string KeyEnvVar = "SCRIPTED_API_KEY";

    /// <summary>The env var a brokered launch's broker address rides, beside the run token on <see cref="KeyEnvVar"/>.</summary>
    public const string BaseUrlEnvVar = "SCRIPTED_BASE_URL";

    /// <summary>The line a continuation prints after the restored session — the proof it ran to the end.</summary>
    public const string ResumedText = "resumed from the restored session";

    private const string ConfigHomeEnvVar = "CS_TEST_CONFIG_DIR";
    private const string GoalEnvVar = "SCRIPTED_GOAL";
    private static readonly ClaudeCodeHarness Layout = new();
    private readonly string _sessionId;
    private readonly string? _releaseFile;

    public SessionKeepingCli(string sessionId, string? releaseFile = null)
    {
        _sessionId = sessionId;
        _releaseFile = releaseFile;
    }

    /// <summary>The task each launch was built from: what the agent was actually handed.</summary>
    public List<AgentTask> Invocations { get; } = [];

    public string Kind => HarnessKind;
    public string Version => "test";
    public IReadOnlyList<string> Models { get; } = ["test-model"];
    public IReadOnlyList<string> SupportedProviders { get; } = ["Anthropic"];

    public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) => new Dictionary<string, string> { [KeyEnvVar] = credential.ApiKey ?? "" };

    public IReadOnlyDictionary<string, string> ProjectBrokered(BrokeredModelCredential brokered) => new Dictionary<string, string> { [BaseUrlEnvVar] = brokered.BaseUrl, [KeyEnvVar] = brokered.RunToken };

    public string? SessionTranscriptRelativePath(string configHome, string? workspaceDirectory, string? sessionId) =>
        ((IAgentSessionTranscript)Layout).SessionTranscriptRelativePath(configHome, workspaceDirectory, sessionId);

    public SandboxSpec BuildInvocation(AgentTask task)
    {
        Invocations.Add(task);

        // A continuation restores the session it was told to resume, where the production layout puts it — as the real
        // harness does — so a resume payload naming the wrong session would leave this CLI nothing to read.
        var relative = SessionTranscriptRelativePath("", task.WorkspaceDirectory, task.ResumeFromSessionId ?? _sessionId) ?? throw new InvalidOperationException("fixture check: the production layout could not address this run's session");
        var session = $"\"${ConfigHomeEnvVar}/{relative}\"";
        var restored = task.RestoredTranscript is { Length: > 0 } transcript ? transcript : null;

        return new SandboxSpec
        {
            Command = "/bin/sh",
            Args = ["-c", restored is null ? FreshScript(session, relative) : ContinueScript(session)],
            WorkingDirectory = task.WorkspaceDirectory,
            TimeoutSeconds = 120,
            ConfigHomeEnvVars = [ConfigHomeEnvVar],
            Environment = new Dictionary<string, string>(task.Environment) { [GoalEnvVar] = task.Goal },
            ConfigHomeFiles = restored is null ? [] : [new ConfigHomeFile { RelativePath = relative, Content = restored }],
        };
    }

    /// <summary>Write the session first, then — once released, when a release file was given — name it on stdout, then stay alive long enough for the drain tick to checkpoint it.</summary>
    private string FreshScript(string session, string relative) =>
        $"mkdir -p \"${ConfigHomeEnvVar}/{Path.GetDirectoryName(relative)!.Replace('\\', '/')}\" && "
        + $"printf '{{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{_sessionId}\"}}\\n' > {session} && "
        + $"printf '{{\"type\":\"user\",\"session_id\":\"{_sessionId}\",\"tool_result\":\"%s\"}}\\n' \"${KeyEnvVar}\" >> {session} && "
        + $"printf '{{\"type\":\"assistant\",\"session_id\":\"{_sessionId}\",\"text\":\"%s\"}}\\n' \"${GoalEnvVar}\" >> {session} && "
        + (_releaseFile is null ? "" : $"while [ ! -f '{_releaseFile}' ]; do sleep 0.1; done && ")
        + $"printf '{{\"type\":\"assistant\",\"session_id\":\"{_sessionId}\",\"text\":\"working\"}}\\n' && sleep 2";

    /// <summary>Read the restored session back, line for line, the way a resuming CLI loads it — then continue.</summary>
    private string ContinueScript(string session) =>
        $"test -f {session} || {{ echo 'restored session missing' >&2; exit 3; }}; "
        + $"while IFS= read -r line; do printf '%s\\n' \"$line\"; done < {session}; "
        + $"printf '{{\"type\":\"assistant\",\"session_id\":\"{_sessionId}\",\"text\":\"{ResumedText}\"}}\\n'";

    public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
        string.IsNullOrWhiteSpace(rawLine) ? [] : [new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim(), Data = TryParse(rawLine) }];

    public IAgentEventFolder CreateFolder() => ScriptedFolders.Result();

    private static JsonElement? TryParse(string rawLine)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(rawLine); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Seed what a launch needs to run a <see cref="SessionKeepingCli"/> agent for real: a team whose owner's authority
    /// receipt the executor accepts, a model credential holding <paramref name="modelKey"/>, and a QUEUED run whose task
    /// is sealed exactly as the agent-run store seals one that embeds <paramref name="workflowSecret"/>.
    /// </summary>
    public static async Task<(Guid TeamId, Guid RunId)> SeedQueuedRunAsync(PostgresFixture fixture, string modelKey, string workflowSecret, Func<AgentTask, AgentTask>? shape = null)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var (teamId, owner) = await SeedTeamAsync(db);
        var credentialId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.ModelCredential.Add(new ModelCredential { Id = credentialId, TeamId = teamId, Provider = "Anthropic", DisplayName = "scripted", EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(modelKey), Status = CredentialStatus.Active });

        var task = new AgentTask
        {
            Goal = $"Deploy the billing service with {workflowSecret}", Harness = HarnessKind, TimeoutSeconds = 120, ModelCredentialId = credentialId,
            CheckpointSessionTranscript = true, ExecutionAuthority = AuthorityOf(teamId, owner, runId), WorkflowSecrets = [workflowSecret],
        };
        var stored = AgentTaskSecrets.Seal(shape is null ? task : shape(task), scope.Resolve<WorkflowSensitivePayloadSealer>());

        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = HarnessKind, Status = AgentRunStatus.Queued, TaskJson = stored.PublicJson, SealedTaskCiphertext = stored.Ciphertext });
        await db.SaveChangesAsync();

        return (teamId, runId);
    }

    /// <summary>
    /// Drive the REAL executor on <paramref name="runId"/> with this harness and the container's own collaborators — its
    /// checkpointer and payload sealer included. Cancelling <paramref name="cancellationToken"/> is a worker going away:
    /// the executor re-raises it and leaves the run Running for a re-attach. <paramref name="brokered"/> hands it the
    /// container's model-credential broker too, so the credential is fronted as a confining deployment requires rather
    /// than injected — the only way such a deployment launches the run at all.
    /// </summary>
    public static async Task ExecuteAsync(PostgresFixture fixture, Guid runId, IAgentHarness harness, CancellationToken cancellationToken = default, bool brokered = false)
    {
        using var scope = fixture.BeginScope();

        await BuildExecutor(scope, harness, brokered ? scope.Resolve<IModelCredentialBroker>() : null).ExecuteAsync(runId, cancellationToken);
    }

    /// <summary>Drive the REAL executor's re-attach on a reservation the test took, with the same collaborators a launch gets.</summary>
    public static async Task ReattachAsync(PostgresFixture fixture, AgentRunReattachReservation reservation, IAgentHarness harness)
    {
        using var scope = fixture.BeginScope();

        await BuildExecutor(scope, harness, broker: null).ReattachAsync(reservation, CancellationToken.None);
    }

    private static AgentRunExecutor BuildExecutor(ILifetimeScope scope, IAgentHarness harness, IModelCredentialBroker? broker) =>
        new(
            scope.Resolve<IAgentRunService>(), new AgentHarnessRegistry([harness]),
            new HarnessModelReconciler(new AgentHarnessRegistry([harness]), scope.Resolve<Core.Services.Agents.ModelCredentials.IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(), scope.Resolve<Core.Services.Agents.Workspace.IAgentWorkspaceResolver>(), scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<Core.Services.Agents.Workspace.IWorkspaceProviderRegistry>(), scope.Resolve<IAgentRunCompletionNotifier>(), scope.Resolve<IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(), scope.Resolve<Core.Services.Review.IStructuredCritic>(), scope.Resolve<IArtifactOffloader>(), scope.Resolve<IArtifactStore>(),
            scope.Resolve<Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<Core.Services.Agents.Publish.IArtifactManifestStore>(),
            scope.Resolve<Core.Services.Agents.Capture.ICaptureIntentService>(), scope.Resolve<IEnumerable<Core.Services.Agents.Publish.IPublishGuard>>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRunExecutor>.Instance,
            credentialBroker: broker, sessionCheckpointer: scope.Resolve<IAgentSessionTranscriptCheckpointer>(), sealer: scope.Resolve<WorkflowSensitivePayloadSealer>());

    /// <summary>Every complete line of a JSONL text, each parsed — throws on any line a resuming CLI could not read.</summary>
    public static IReadOnlyList<JsonElement> ParseLines(string jsonl) =>
        jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();

    /// <summary>
    /// Every session-transcript checkpoint the run kept, once one of them satisfies <paramref name="landed"/> — or what is
    /// kept when <see cref="CheckpointLandingWait"/> runs out, for the caller's own fixture check to name. A checkpoint
    /// uploads off the run's path and a landing waits only seconds for it, so on a cold store the run can be terminal
    /// while its last checkpoint is still being written.
    /// </summary>
    public static async Task<IReadOnlyList<string>> KeptCheckpointsAsync(PostgresFixture fixture, Guid teamId, Guid runId, Func<string, bool> landed)
    {
        var deadline = DateTimeOffset.UtcNow + CheckpointLandingWait;

        while (true)
        {
            var kept = await ReadKeptCheckpointsAsync(fixture, teamId, runId);

            if (kept.Any(landed) || DateTimeOffset.UtcNow >= deadline) return kept;

            await Task.Delay(200);
        }
    }

    /// <summary>The checkpointer's own upload budget, and some slack: past it no checkpoint is still coming.</summary>
    private static readonly TimeSpan CheckpointLandingWait = AgentRunExecutor.SessionCheckpointUploadBudget + TimeSpan.FromSeconds(15);

    /// <summary>The run's kept checkpoints, read back from where production stores them: the retention rows that hold them, and the artifact store.</summary>
    private static async Task<IReadOnlyList<string>> ReadKeptCheckpointsAsync(PostgresFixture fixture, Guid teamId, Guid runId)
    {
        using var scope = fixture.BeginScope();

        var artifactIds = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking()
            .Where(d => d.TeamId == teamId && d.HolderId == runId && d.RetentionClass == nameof(ArtifactRetentionClass.SessionTranscriptCheckpoint))
            .Select(d => d.ArtifactId).ToListAsync();

        var checkpoints = new List<string>();

        foreach (var artifactId in artifactIds)
            checkpoints.Add(System.Text.Encoding.UTF8.GetString((await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None))!.Bytes));

        return checkpoints;
    }

    private static async Task<(Guid TeamId, (Guid UserId, Guid MembershipId, Guid SecurityStamp) Owner)> SeedTeamAsync(CodeSpaceDbContext db)
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"session-{userId:N}@test.local", Name = $"session-{userId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"session-{teamId:N}", Name = "Session Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = membershipId, TeamId = teamId, UserId = userId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();

        var stamp = (await db.User.AsNoTracking().SingleAsync(u => u.Id == userId)).SecurityStamp;

        return (teamId, (userId, membershipId, stamp));
    }

    private static AgentExecutionAuthority AuthorityOf(Guid teamId, (Guid UserId, Guid MembershipId, Guid SecurityStamp) owner, Guid logicalRunId) => new()
    {
        Version = ExecutionAuthorityService.ReceiptVersion,
        PolicyVersion = ExecutionAuthorityService.PolicyVersion,
        TeamId = teamId,
        LogicalRunId = logicalRunId,
        SourceKind = "standalone",
        DefinitionHash = "",
        GrantedCeiling = AgentAutonomyPolicy.DeploymentCeiling,
        IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        Subjects = [new AgentAuthoritySubject { Kind = "launcher", UserId = owner.UserId, SecurityStamp = owner.SecurityStamp, MembershipId = owner.MembershipId, IssuedRole = TeamRole.Owner, Permission = TeamPermissions.RunsLaunch, GlobalAdmin = false }],
    };
}
