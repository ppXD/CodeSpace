using System.Diagnostics;
using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.StorageTestWorker;
using Shouldly;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>One pod of a burst: a name, the build it runs, and how many callers (runs, deliveries) it has in flight.</summary>
public sealed record PodSpec(string Name, PodBuild Build, int Callers);

/// <summary>What one caller of one pod got back from the refresher.</summary>
public sealed record PodCaller(string Pod, PodBuild Build, int Caller, bool Ok, string? AccessToken, string? Error, string? Message)
{
    public override string ToString() => $"pod {Pod}/{Build} caller {Caller}: {(Ok ? "OK " + AccessToken : "FAILED " + Error + ": " + Message)}";
}

/// <summary>
/// Pods as real OS processes over the fixture's Postgres: each one is <c>CodeSpace.StorageTestWorker</c> running the
/// production OAuth refresher (or the pinned previous build's) with its own static refresh semaphores, its own connection
/// pools and its own DbContexts. Every caller of every pod loads the credential row before any of them refreshes, then
/// they all go at once on the parent's word — the window a burst of webhook deliveries or parallel runs across API and
/// worker pods produces, which an in-process test cannot make: one process shares a single refresh semaphore.
///
/// <para>The parent and the pods share a filesystem Data Protection key ring, so this harness seeds and reads the
/// credential with a hand-wired stack on that ring (<see cref="Stack"/>) and not through the fixture's container, whose
/// key ring is the machine's.</para>
/// </summary>
public sealed class RefreshPodHarness : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private readonly string _connectionString;
    private readonly string _keysDirectory = Directory.CreateTempSubdirectory("cs-refresh-pods-").FullName;
    private readonly List<Process> _children = new();

    public RefreshPodHarness(string connectionString)
    {
        _connectionString = connectionString;
        Stack = PodStack.Build(connectionString, _keysDirectory);
    }

    public PodStack Stack { get; }

    /// <summary>A team's OAuth credential holding the given pair, on an instance whose OAuth client is the stub's default application. The pair is not put in the stub.</summary>
    public async Task<Guid> SeedCredentialAsync(string gitLabBaseUrl, string accessToken, string refreshToken, bool expired = true)
    {
        await using var db = Stack.NewDb();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "nacho" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "gitlab", BaseUrl = gitLabBaseUrl, ApiUrl = gitLabBaseUrl, OauthClientId = GitLabDoorkeeperStub.DefaultClientId, OauthClientSecretEnc = Stack.Encryptor.Encrypt(GitLabDoorkeeperStub.DefaultClientSecret) };
        var payload = new OAuthPayload { AccessToken = accessToken, RefreshToken = refreshToken, ExpiresAt = expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(2) };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, AuthType = AuthType.OAuth, DisplayName = "GitLab · NACHO.L", EncryptedPayload = Stack.Encryptor.Encrypt(Stack.Serializer.Serialize(payload)), ExpiresDate = payload.ExpiresAt, Status = CredentialStatus.Active };

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return credential.Id;
    }

    /// <summary>Starts the pods, waits until every caller of every one holds the row it will refresh with, lets them all go at once, and returns what each got.</summary>
    public async Task<IReadOnlyList<PodCaller>> RunAsync(Guid credentialId, params PodSpec[] pods)
    {
        using var deadline = new CancellationTokenSource(Patience);
        var running = pods.Select(pod => (Pod: pod, Process: Start(pod, credentialId))).ToList();

        foreach (var (pod, process) in running)
            (await ReadLineAsync(process, deadline.Token).ConfigureAwait(false)).ShouldBe("ready", $"pod {pod.Name} did not reach its 'ready' barrier; run it by hand with `dotnet exec <IntegrationTests bin>/CodeSpace.StorageTestWorker.dll {OAuthRefreshPodWorker.Mode} <connection string> {_keysDirectory} {credentialId} {pod.Callers} {pod.Build}` and answer its barrier");

        foreach (var (_, process) in running)
        {
            await process.StandardInput.WriteLineAsync("go").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        var results = new List<PodCaller>();

        foreach (var (pod, process) in running)
        {
            var output = await process.StandardOutput.ReadToEndAsync(deadline.Token).ConfigureAwait(false);

            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            process.ExitCode.ShouldBe(0, $"pod {pod.Name} failed: {await process.StandardError.ReadToEndAsync(deadline.Token).ConfigureAwait(false)}");

            results.AddRange(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("RESULT ", StringComparison.Ordinal)).Select(line => Parse(pod, line["RESULT ".Length..])));
        }

        results.Count.ShouldBe(pods.Sum(p => p.Callers), "every caller of every pod reports a result");

        return results;
    }

    private Process Start(PodSpec pod, Guid credentialId)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "CodeSpace.IntegrationTests.runtimeconfig.json"), "--depsfile", Path.Combine(AppContext.BaseDirectory, "CodeSpace.IntegrationTests.deps.json"), typeof(OAuthRefreshPodWorker).Assembly.Location, OAuthRefreshPodWorker.Mode, _connectionString, _keysDirectory, credentialId.ToString(), pod.Callers.ToString(), pod.Build.ToString() }) start.ArgumentList.Add(argument);

        var child = Process.Start(start) ?? throw new InvalidOperationException($"Could not start pod {pod.Name}.");

        _children.Add(child);

        return child;
    }

    private static async Task<string> ReadLineAsync(Process pod, CancellationToken deadline) =>
        await pod.StandardOutput.ReadLineAsync(deadline).ConfigureAwait(false) ?? throw new Xunit.Sdk.XunitException($"a pod exited before its next line: {await pod.StandardError.ReadToEndAsync(deadline).ConfigureAwait(false)}");

    private static PodCaller Parse(PodSpec pod, string json)
    {
        var result = JsonDocument.Parse(json).RootElement;

        string? Text(string name) => result.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        return new PodCaller(pod.Name, pod.Build, result.GetProperty("caller").GetInt32(), result.GetProperty("ok").GetBoolean(), Text("accessToken"), Text("error"), Text("message"));
    }

    public void Dispose()
    {
        foreach (var child in _children)
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { /* best-effort: it may have exited between the check and the kill */ }

            child.Dispose();
        }

        try { Directory.Delete(_keysDirectory, recursive: true); } catch { /* best-effort */ }
    }
}
