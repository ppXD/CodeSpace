using System.Diagnostics;
using Autofac;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using MediatR;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — what the recurring credential health probe costs a worker when a provider stops answering, from the real
/// <c>ProbeCredentialsCommand</c> through the real GitLab provider to a loopback server that accepts the connection and never
/// replies (<see cref="SilentProviderHost"/>), over real Postgres. The sweep runs on the control pool beside webhooks and
/// resume; a probe that waits on a silent provider for the HTTP client's whole timeout (five minutes, three attempts) holds a
/// worker for a quarter of an hour, and a tick every ten minutes then piles up behind it until the pool has none left.
///
/// <para>What it pins: one credential costs the sweep at most the probe deadline, and nothing is recorded about it (no answer
/// says nothing about a token); the whole pass ends at its budget, in-flight probe included, ahead of the next tick; a probe
/// that ignores the token it was given — a provider SDK with no cancellation, a call that blocks its thread — is walked
/// away from at the deadline all the same; and a tick that finds the previous pass still running skips instead of piling
/// up. The deadlines are shortened by registering a <see cref="CredentialHealthDeadlines"/>, so no test waits a minute.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CredentialHealthDeadlineFlowTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CredentialHealthDeadlineFlowTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task A_provider_that_never_answers_costs_the_sweep_its_probe_deadline_and_nothing_is_recorded_about_the_credential()
    {
        using var silent = new SilentProviderHost();
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, silent.BaseUrl, "glpat-silent").ConfigureAwait(false);
        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        var (probed, took) = await TimedSweepAsync(new CredentialHealthDeadlines(Probe: TimeSpan.FromSeconds(1), Pass: TimeSpan.FromMinutes(1))).ConfigureAwait(false);

        _output.WriteLine($"sweep took {took.TotalSeconds:0.0}s; connections={silent.Connections}");
        took.ShouldBeLessThan(Generous, "the sweep gives up on a silent provider at the probe deadline, not at the HTTP client's five minutes");
        probed.ShouldBe(0, "a credential the provider did not answer about is not one it answered about");
        silent.Connections.ShouldBe(1, "a call given up on at its deadline is not sent again");

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Active, "no answer is not a verdict: marking the token dead on a deadline would stop every run through it");
        row.LastValidatedDate.ShouldBeNull("and it stays due, so the next tick asks again");
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task A_pass_ends_at_its_budget_with_a_probe_still_in_flight_and_does_not_go_on_to_the_next_credential()
    {
        using var silent = new SilentProviderHost();
        var seeds = new List<SeededGitLabCredential>();

        for (var i = 0; i < 3; i++) seeds.Add(await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, silent.BaseUrl, $"glpat-silent-{i}").ConfigureAwait(false));

        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, seeds.Select(s => s.CredentialId).ToArray()).ConfigureAwait(false);

        var (probed, took) = await TimedSweepAsync(new CredentialHealthDeadlines(Probe: TimeSpan.FromSeconds(30), Pass: TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        _output.WriteLine($"sweep took {took.TotalSeconds:0.0}s; connections={silent.Connections}");
        took.ShouldBeLessThan(TimeSpan.FromSeconds(10), "the budget cuts the probe that is in flight; waiting for its 30-second deadline would overrun it");
        probed.ShouldBe(0);
        silent.Connections.ShouldBe(1, "a pass that is out of budget starts no further probe");

        foreach (var seed in seeds)
            (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active, "a pass that ran out of time condemns nobody");
    }

    [Theory]
    [InlineData(ProbeIgnores.TheTokenWhileAwaiting)]
    [InlineData(ProbeIgnores.TheTokenWhileBlockingItsThread)]
    public async Task A_probe_that_ignores_its_token_is_walked_away_from_at_the_deadline(ProbeIgnores how)
    {
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, "http://127.0.0.1:1", "glpat-ignoring").ConfigureAwait(false);
        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);
        var calls = new ProbeCalls();
        var registry = new ProbeOnlyRegistry(new UncancellableProbe(how, calls));

        var (probed, took) = await TimedSweepAsync(new CredentialHealthDeadlines(Probe: TimeSpan.FromSeconds(1), Pass: TimeSpan.FromMinutes(1)), registry).ConfigureAwait(false);

        _output.WriteLine($"{how}: sweep took {took.TotalSeconds:0.0}s");
        took.ShouldBeLessThan(TimeSpan.FromSeconds(10), "a provider SDK that takes no token cannot be cancelled, but the sweep need not wait for it");
        probed.ShouldBe(0);
        calls.Started.ShouldBe(1);
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active);
    }

    [Fact]
    public async Task A_tick_that_finds_the_previous_pass_still_running_skips_instead_of_piling_up_behind_it()
    {
        using var silent = new SilentProviderHost();
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, silent.BaseUrl, "glpat-overlap").ConfigureAwait(false);
        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);
        var deadlines = new CredentialHealthDeadlines(Probe: TimeSpan.FromSeconds(1), Pass: TimeSpan.FromMinutes(1));

        IAsyncDisposable runningPass;

        using (var scope = _fixture.BeginScope())
            runningPass = (await scope.Resolve<ICrossProcessLock>().AcquireAsync(CredentialHealthService.PassLockKey, CancellationToken.None).ConfigureAwait(false));

        var (skipped, took) = await TimedSweepAsync(deadlines).ConfigureAwait(false);

        took.ShouldBeLessThan(TimeSpan.FromSeconds(5), "a skipped tick returns at once; it does not wait for the pass it overlaps");
        skipped.ShouldBe(0);
        silent.Connections.ShouldBe(0, "while a pass is running, a second one asks no provider anything");

        await runningPass.DisposeAsync().ConfigureAwait(false);

        await TimedSweepAsync(deadlines).ConfigureAwait(false);

        silent.Connections.ShouldBe(1, "once the pass is over the next tick runs");
    }

    public enum ProbeIgnores { TheTokenWhileAwaiting, TheTokenWhileBlockingItsThread }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private async Task<(int Probed, TimeSpan Took)> TimedSweepAsync(CredentialHealthDeadlines deadlines, IProviderRegistry? registry = null)
    {
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(deadlines).SingleInstance();

            if (registry is not null) b.RegisterInstance(registry).As<IProviderRegistry>().SingleInstance();
        });

        var clock = Stopwatch.StartNew();

        // On a thread of its own, bounded: a sweep that ignores its deadlines must fail the test rather than hang the suite.
        var sweep = Task.Run(() => scope.Resolve<IMediator>().Send(new ProbeCredentialsCommand { BatchSize = 25 }));

        if (await Task.WhenAny(sweep, Task.Delay(TimeSpan.FromSeconds(45))) != sweep) throw new ShouldAssertException("the sweep was still running after 45 seconds — nothing bounded it");

        return (await sweep.ConfigureAwait(false), clock.Elapsed);
    }

    /// <summary>A registry that answers the credential probe with <see cref="UncancellableProbe"/> for every provider.</summary>
    private sealed class ProbeOnlyRegistry : IProviderRegistry
    {
        private readonly UncancellableProbe _probe;

        public ProbeOnlyRegistry(UncancellableProbe probe) { _probe = probe; }

        public TCapability Require<TCapability>(ProviderKind kind) where TCapability : IProviderCapability => (TCapability)(object)_probe;

        public bool TryGet<TCapability>(ProviderKind kind, out TCapability? capability) where TCapability : class, IProviderCapability
        {
            capability = _probe as TCapability;

            return capability is not null;
        }

        public IReadOnlyList<Type> GetCapabilities(ProviderKind kind) => new[] { typeof(ICredentialProbeCapability) };
    }

    private sealed class ProbeCalls
    {
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public void Start() => Interlocked.Increment(ref _started);
    }

    /// <summary>
    /// A provider call that takes no cancellation token, as an SDK built on an HTTP client with no per-call token does — and, in
    /// the second shape, one that blocks the thread that calls it. A struct, not a class: the fixture registers every class
    /// of this assembly that is a provider capability, for every test, and this one must exist only in the registry the test
    /// hands the sweep.
    /// </summary>
    private readonly struct UncancellableProbe : ICredentialProbeCapability
    {
        private readonly ProbeIgnores _how;
        private readonly ProbeCalls _calls;

        public UncancellableProbe(ProbeIgnores how, ProbeCalls calls)
        {
            _how = how;
            _calls = calls;
        }

        public ProviderKind Kind => ProviderKind.GitLab;

        public Task<CredentialProbeResult> ProbeCredentialAsync(ProviderContext context, CancellationToken cancellationToken)
        {
            _calls.Start();

            if (_how == ProbeIgnores.TheTokenWhileBlockingItsThread) Thread.Sleep(TimeSpan.FromSeconds(30));

            return Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None).ContinueWith(_ => new CredentialProbeResult { IsValid = true }, TaskScheduler.Default);
        }
    }
}
