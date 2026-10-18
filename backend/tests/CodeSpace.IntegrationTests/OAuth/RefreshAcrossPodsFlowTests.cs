using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.StorageTestWorker;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// 🟢 High fidelity: bursts of callers across pods — separate OS processes with their own refresh semaphores, connection
/// pools and DbContexts, all against one Postgres and one <see cref="GitLabDoorkeeperStub"/> — and across BUILDS. A rolling
/// deploy, or a rollback, runs pods of this build beside pods of the one before it; each reads the credential rows the other
/// wrote, and they must take the same refresh lock. The previous build is the pinned copy of its refresher, payload writer
/// and lock (<c>CodeSpace.StorageTestWorker/PreviousBuild</c>) — the lock there still on pooled connections, this build's
/// on connections of its own.
///
/// <para>Nothing here changes a row's shape: the payload is the same Data Protection envelope, the store is the same
/// compare-and-swap on the same column, and the lock is the same advisory key, so the builds interoperate in either
/// direction.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RefreshAcrossPodsFlowTests
{
    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RefreshAcrossPodsFlowTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task S1_six_callers_over_two_pods_of_this_build_refresh_once()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        using var pods = new RefreshPodHarness(_fixture.ConnectionString);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var credentialId = await pods.SeedCredentialAsync(gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        var callers = await pods.RunAsync(credentialId, new PodSpec("A", PodBuild.This, 3), new PodSpec("B", PodBuild.This, 3)).ConfigureAwait(false);

        await ShouldHaveRefreshedOnceAsync(gitlab, pods, credentialId, callers).ConfigureAwait(false);
    }

    [Fact]
    public async Task A_pod_of_the_previous_build_and_a_pod_of_this_one_share_one_refresh()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        using var pods = new RefreshPodHarness(_fixture.ConnectionString);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var credentialId = await pods.SeedCredentialAsync(gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        var callers = await pods.RunAsync(credentialId, new PodSpec("old", PodBuild.Previous, 3), new PodSpec("new", PodBuild.This, 3)).ConfigureAwait(false);

        await ShouldHaveRefreshedOnceAsync(gitlab, pods, credentialId, callers).ConfigureAwait(false);
    }

    [Fact]
    public async Task Each_build_refreshes_from_the_row_the_other_wrote()
    {
        // Every token lasts a minute, inside the refresher's two-minute buffer, so each pod must refresh from what the
        // pod before it stored: this build's pair is read and rotated by the previous build, and that one's by this build.
        using var gitlab = new GitLabDoorkeeperStub { ExpiresInSeconds = 60 };
        using var pods = new RefreshPodHarness(_fixture.ConnectionString);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var credentialId = await pods.SeedCredentialAsync(gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        var thisBuild = await pods.RunAsync(credentialId, new PodSpec("new-1", PodBuild.This, 1)).ConfigureAwait(false);
        var previousBuild = await pods.RunAsync(credentialId, new PodSpec("old", PodBuild.Previous, 1)).ConfigureAwait(false);
        var thisBuildAgain = await pods.RunAsync(credentialId, new PodSpec("new-2", PodBuild.This, 1)).ConfigureAwait(false);

        Dump(gitlab, thisBuild.Concat(previousBuild).Concat(thisBuildAgain));
        gitlab.Count(StubEvent.Refused).ShouldBe(0, $"a pod presented a refresh token GitLab had already consumed: {string.Join(", ", gitlab.RefusedRefreshTokens())}");
        gitlab.Count(StubEvent.Rotated).ShouldBe(3, "one refresh per pod");
        thisBuild.Concat(previousBuild).Concat(thisBuildAgain).ShouldAllBe(c => c.Ok);
        (await pods.Stack.ReadStoredAsync(credentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-4", "the last pod's pair is the row's");
    }

    [Fact]
    public async Task A_pod_of_the_previous_build_uses_the_pair_this_build_stored_without_refreshing_again()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        using var pods = new RefreshPodHarness(_fixture.ConnectionString);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var credentialId = await pods.SeedCredentialAsync(gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        await pods.RunAsync(credentialId, new PodSpec("new", PodBuild.This, 1)).ConfigureAwait(false);
        var previousBuild = await pods.RunAsync(credentialId, new PodSpec("old", PodBuild.Previous, 2)).ConfigureAwait(false);

        previousBuild.ShouldAllBe(c => c.Ok && c.AccessToken == "at-2", "the previous build reads the pair this build sealed and finds it live");
        gitlab.Count(StubEvent.Rotated).ShouldBe(1);
        gitlab.Count(StubEvent.Refused).ShouldBe(0);
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private async Task ShouldHaveRefreshedOnceAsync(GitLabDoorkeeperStub gitlab, RefreshPodHarness pods, Guid credentialId, IReadOnlyList<PodCaller> callers)
    {
        Dump(gitlab, callers);
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "one refresh for the whole burst");
        gitlab.Count(StubEvent.Refused).ShouldBe(0, $"a caller presented the refresh token the first refresh consumed: {string.Join(", ", gitlab.RefusedRefreshTokens())}");
        callers.ShouldAllBe(c => c.Ok && c.AccessToken == "at-2", string.Join(" | ", callers.Select(c => c.ToString())));
        (await pods.Stack.ReadStoredAsync(credentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-2", "the row holds the pair GitLab issued");
        gitlab.IsAccessTokenLive("at-0").ShouldBeFalse("fixture check: the refresh killed the access token it replaced");
    }

    private void Dump(GitLabDoorkeeperStub gitlab, IEnumerable<PodCaller> callers)
    {
        foreach (var caller in callers) _output.WriteLine(caller.ToString());

        foreach (var entry in gitlab.Log) _output.WriteLine($"  {entry.At:HH:mm:ss.fff} {entry.Kind}: {entry.Detail}");
    }
}
