using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Credentials;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the model-credential brokerage: what a brokered run's environment may contain, and what the broker answers
/// once a lease is revoked, expired or superseded.
///
/// <para>Each test drives the REAL <see cref="LoopbackModelCredentialBroker"/> over a real HTTP loopback listener,
/// with a stub <see cref="HttpMessageHandler"/> standing in for the provider — so the request the "provider" receives
/// is asserted exactly (the tenant's key attached server-side, the run token nowhere in it), which is the whole claim
/// this slice makes and the one a mocked proxy could not check.</para>
/// </summary>
[Trait("Category", "Unit")]
public class ModelCredentialBrokerTests
{
    private const string UpstreamKey = "sk-ant-the-tenants-real-and-long-lived-key";

    private static ResolvedModelCredential AnthropicCredential() => new() { Provider = "Anthropic", ApiKey = UpstreamKey };

    private static ModelCredentialLeaseRequest LeaseFor(Guid runId, long epoch = 7, TimeSpan? ttl = null) => new()
    {
        RunId = runId, TeamId = Guid.NewGuid(), Epoch = epoch, Upstream = AnthropicCredential(), Ttl = ttl ?? TimeSpan.FromMinutes(3),
    };

    // ── The projection: a brokered run's env carries a token, never the key ───────────────────────────────────────

    [Theory]
    [InlineData("claude-code", ClaudeCodeHarness.BaseUrlEnvVar, ClaudeCodeHarness.AuthTokenEnvVar)]
    [InlineData("codex-cli", CodexHarness.BaseUrlEnvVar, CodexHarness.ApiKeyEnvVar)]
    public void A_brokered_projection_carries_the_broker_and_the_run_token_and_never_the_upstream_key(string harnessKind, string expectedBaseUrlVar, string expectedTokenVar)
    {
        var brokered = new BrokeredModelCredential("http://127.0.0.1:44444/abc123", "run-token-not-the-key", DateTimeOffset.UtcNow.AddMinutes(3));

        var env = ProjectorFor(harnessKind).ProjectBrokered(brokered);

        env.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(new[] { expectedBaseUrlVar, expectedTokenVar }.OrderBy(k => k, StringComparer.Ordinal),
            customMessage: "a brokered projection must emit exactly the base URL + the run token — an extra variable is an extra carrier nobody audited");

        env[expectedBaseUrlVar].ShouldBe("http://127.0.0.1:44444/abc123");
        env[expectedTokenVar].ShouldBe("run-token-not-the-key");

        // The mutation this exists to kill: a projection that "helpfully" also passes the upstream key through.
        env.Values.ShouldNotContain(UpstreamKey, "the upstream provider key must never appear in a brokered projection — the sandbox is not given one");
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex-cli")]
    public void A_brokered_projection_never_reuses_the_direct_api_key_variable_of_a_key_carrying_harness(string harnessKind)
    {
        // Claude Code's api-key variable is x-api-key-shaped and would send the token as if it were an Anthropic key;
        // Codex has only the one variable, so this pins the (different) correct answer for each rather than a shared one.
        var env = ProjectorFor(harnessKind).ProjectBrokered(new("http://127.0.0.1:1/r", "token", DateTimeOffset.UtcNow.AddMinutes(1)));

        if (harnessKind == ClaudeCodeHarness.HarnessKind)
            env.ShouldNotContainKey(ClaudeCodeHarness.ApiKeyEnvVar, "a brokered Claude run authenticates to the broker as a gateway, on the auth-token carrier");
        else
            env.ShouldContainKey(CodexHarness.ApiKeyEnvVar, "Codex has exactly one credential carrier, and the run token rides it");
    }

    private static IBrokeredModelCredentialProjector ProjectorFor(string harnessKind) =>
        harnessKind == ClaudeCodeHarness.HarnessKind ? new ClaudeCodeHarness() : new CodexHarness();

    // ── The lease: what the broker answers, and when it stops ─────────────────────────────────────────────────────

    /// <summary>
    /// Both carriers a harness projection can put the bearer on, because the DROP is per-header and a header left
    /// out of <c>DroppedRequestHeaders</c> is forwarded: a token arriving on <c>x-api-key</c> would then reach the
    /// provider ALONGSIDE the tenant's key on the same header. That is the mutation the <c>Single()</c> below kills —
    /// remove <c>"x-api-key"</c> from the drop set and the upstream sees two values, not one.
    /// </summary>
    [Theory]
    [InlineData("Authorization")]
    [InlineData(ApiKeyCarrier)]
    public async Task A_live_lease_relays_the_call_with_the_tenants_key_attached_server_side(string carrier)
    {
        var upstream = new StubUpstream();
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var runId = Guid.NewGuid();

        var brokered = await broker.OpenAsync(LeaseFor(runId), CancellationToken.None);
        if (brokered is null) return;   // this host cannot bind a loopback listener at all — nothing to assert

        var response = await CallAsync(brokered, "/v1/messages", brokered.RunToken, carrier);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        upstream.LastRequest.ShouldNotBeNull();
        upstream.LastRequest!.RequestUri!.ToString().ShouldBe("https://api.anthropic.com/v1/messages",
            customMessage: "the provider's default endpoint plus the path the CLI asked for — a doubled or dropped /v1 is a 404 the operator sees as a broken model");
        upstream.LastRequest.Headers.GetValues("x-api-key").Single().ShouldBe(UpstreamKey, "an Anthropic upstream authenticates by x-api-key, attached here and only here");
        upstream.LastRequest.Headers.Contains("Authorization").ShouldBeFalse("the run token must not be forwarded — the upstream key replaces it, it does not join it");
        upstream.SeenHeaderValues.ShouldNotContain(brokered.RunToken, "the per-run bearer authenticates to the broker only; forwarding it leaks a capability the provider has no use for");
    }

    [Fact]
    public async Task A_revoked_lease_is_refused()
    {
        var upstream = new StubUpstream();
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var runId = Guid.NewGuid();

        var brokered = await broker.OpenAsync(LeaseFor(runId), CancellationToken.None);
        if (brokered is null) return;

        (await CallAsync(brokered, "/v1/messages", brokered.RunToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await broker.RevokeAsync(runId, "run-cancelled", CancellationToken.None);

        (await RefusedAsync(brokered, brokered.RunToken)).ShouldBeTrue(
            "a revoked lease must refuse the NEXT call — revocation that only takes effect when the process dies is the defect this slice exists to remove");
        upstream.Calls.ShouldBe(1, "a refused call must never reach the provider, or the tenant is billed for a run that was cancelled");
    }

    [Fact]
    public async Task A_lease_left_unrenewed_past_its_ttl_is_refused_and_a_renewal_keeps_it_alive()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var upstream = new StubUpstream();
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream, time);
        var runId = Guid.NewGuid();
        var ttl = TimeSpan.FromMinutes(2);

        var brokered = await broker.OpenAsync(LeaseFor(runId, ttl: ttl), CancellationToken.None);
        if (brokered is null) return;

        // A renewal inside the window carries the lease past the ORIGINAL expiry — the heartbeat's whole job.
        time.Advance(ttl - TimeSpan.FromSeconds(1));
        (await broker.RenewAsync(runId, 7, CancellationToken.None)).ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(2));
        (await CallAsync(brokered, "/v1/messages", brokered.RunToken)).StatusCode.ShouldBe(HttpStatusCode.OK,
            customMessage: "a renewed lease must survive its original expiry, or every long run 401s mid-turn");

        // Silence for a full TTL is a worker that stopped owning the run: the key stops being spendable on its own.
        time.Advance(ModelCredentialLease.Ttl + TimeSpan.FromSeconds(1));

        (await CallAsync(brokered, "/v1/messages", brokered.RunToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            customMessage: "an unrenewed lease must lapse — a CLI outliving its worker is exactly the case where nobody is left to kill it");
    }

    [Fact]
    public async Task A_renewal_or_a_token_from_a_superseded_epoch_is_refused()
    {
        var upstream = new StubUpstream();
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var runId = Guid.NewGuid();

        var first = await broker.OpenAsync(LeaseFor(runId, epoch: 7), CancellationToken.None);
        if (first is null) return;

        (await broker.RenewAsync(runId, 8, CancellationToken.None)).ShouldBeFalse("a renewal that does not present the lease's own fence must not extend it");

        // The run was reclaimed: a new attempt opens at a higher epoch, and the superseded attempt's token dies now
        // rather than at the end of a TTL its own worker could still be renewing.
        var second = await broker.OpenAsync(LeaseFor(runId, epoch: 8), CancellationToken.None);

        second!.RunToken.ShouldNotBe(first.RunToken);
        second.RebindPort.ShouldNotBe(first.RebindPort, "a superseding attempt takes its OWN address; reusing the old one would race the close that withdraws it");
        (await RefusedAsync(first, first.RunToken)).ShouldBeTrue(
            "the superseded attempt's bearer must stop working the moment the run is re-claimed");
        (await CallAsync(second, "/v1/messages", second.RunToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_call_presenting_no_or_a_wrong_token_is_refused()
    {
        var upstream = new StubUpstream();
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);

        var brokered = await broker.OpenAsync(LeaseFor(Guid.NewGuid()), CancellationToken.None);
        if (brokered is null) return;

        (await CallAsync(brokered, "/v1/messages", token: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(brokered, "/v1/messages", "not-the-run-token-but-long-enough")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        upstream.Calls.ShouldBe(0, "an unauthenticated call must never be relayed");
    }

    [Fact]
    public async Task Another_runs_token_cannot_spend_this_runs_lease()
    {
        var upstream = new StubUpstream();
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);

        var mine = await broker.OpenAsync(LeaseFor(Guid.NewGuid()), CancellationToken.None);
        if (mine is null) return;
        var theirs = await broker.OpenAsync(LeaseFor(Guid.NewGuid()), CancellationToken.None);

        (await CallAsync(mine, "/v1/messages", theirs!.RunToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            customMessage: "a token is scoped to its own run's route; one run replaying another's would make every lease a shared key");
    }

    [Fact]
    public async Task A_streamed_response_reaches_the_caller_chunk_by_chunk()
    {
        var upstream = new StubUpstream { Streamed = true };
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);

        var brokered = await broker.OpenAsync(LeaseFor(Guid.NewGuid()), CancellationToken.None);
        if (brokered is null) return;

        // Queued before the call so the relay has a first chunk to write: the listener sends the response head with
        // the first body write, so a test that withheld every chunk would be waiting on itself, not on the relay.
        upstream.Push("data: one\n\n");

        using var client = new HttpClient();
        using var request = Authorized(brokered, "/v1/messages", brokered.RunToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

        await using var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[256];

        // The SECOND event is pushed only after the first has been read out of the relay. Receiving it before the
        // upstream body ends is what proves the proxy forwards + flushes per chunk: a relay that buffered the body to
        // completion could not deliver anything until Complete() below, and in production would turn a live token
        // stream into one late blob.
        (await ReadTextAsync(body, buffer)).ShouldBe("data: one\n\n");

        upstream.Push("data: two\n\n");
        (await ReadTextAsync(body, buffer)).ShouldBe("data: two\n\n");

        upstream.Complete();
        (await body.ReadAsync(buffer)).ShouldBe(0);
    }

    private static async Task<string> ReadTextAsync(Stream body, byte[] buffer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var read = await body.ReadAsync(buffer, timeout.Token);

        read.ShouldBeGreaterThan(0, "the relay delivered nothing within 10s — a buffered (non-streaming) copy would stall exactly here; check LoopbackModelCredentialBroker.CopyBodyAsync's per-chunk flush");

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    // ── The address, and re-opening it after the worker that minted it is gone ────────────────────────────────────

    [Fact]
    public async Task An_open_hands_back_the_port_and_route_its_own_base_url_names()
    {
        using var broker = LoopbackModelCredentialBroker.ForTest(new StubUpstream());

        var brokered = await broker.OpenAsync(LeaseFor(Guid.NewGuid()), CancellationToken.None);
        if (brokered is null) return;

        var port = brokered.RebindPort.ShouldNotBeNull("without the port, nothing reaches the run's durable handle and every deploy ends every in-flight brokered run — there is no address left to re-open");
        var route = brokered.RebindRoute.ShouldNotBeNull("without the route, a re-bind would have to mint one, and the agent's frozen base URL names the old one");

        brokered.BaseUrl.ShouldBe($"http://{SandboxSpec.ModelBrokerHostToken}:{port}/{route}",
            customMessage: "the coordinates handed back for the handle must be exactly the ones the CHILD was given — a port naming some other listener re-binds an address nobody calls, and the failure surfaces one deploy later as a run that will not talk");
    }

    [Fact]
    public async Task Two_runs_are_brokered_on_two_ports_so_one_conflict_cannot_take_out_the_other()
    {
        using var broker = LoopbackModelCredentialBroker.ForTest(new StubUpstream());

        var first = await broker.OpenAsync(LeaseFor(Guid.NewGuid()), CancellationToken.None);
        if (first is null) return;
        var second = await broker.OpenAsync(LeaseFor(Guid.NewGuid()), CancellationToken.None);

        second!.RebindPort.ShouldNotBe(first.RebindPort,
            customMessage: "a port per run is the design, not an accident: on ONE worker port a single conflict — a port an unrelated process already holds, a second worker on the same host — takes out every in-flight brokered run at once, where a port per lease fails exactly one run");
    }

    [Fact]
    public async Task Rebind_restores_the_recorded_port_route_and_token_so_the_original_base_url_still_works()
    {
        var runId = Guid.NewGuid();
        BrokeredModelCredential brokered;

        // Worker A mints the address and then GOES AWAY. What survives it is only what the run's durable handle
        // carries — the port, the route, the bearer — which is exactly what worker B is given below.
        using (var workerA = LoopbackModelCredentialBroker.ForTest(new StubUpstream()))
        {
            if (await workerA.OpenAsync(LeaseFor(runId, epoch: 7), CancellationToken.None) is not { } opened) return;

            brokered = opened;
            (await CallAsync(brokered, "/v1/messages", brokered.RunToken)).StatusCode.ShouldBe(HttpStatusCode.OK, "precondition: the address answers while the worker that minted it holds it");
        }

        (await RefusedAsync(brokered, brokered.RunToken)).ShouldBeTrue("precondition: the address died with worker A — that IS the problem this re-bind exists for");

        var upstream = new StubUpstream();
        using var workerB = LoopbackModelCredentialBroker.ForTest(upstream);

        (await workerB.RebindAsync(RebindOf(brokered, runId, epoch: 8), CancellationToken.None)).ShouldBeTrue(
            "worker B must be able to re-open the address the detached agent is still calling; if it cannot, every deploy ends every brokered run in flight");

        var response = await CallAsync(brokered, "/v1/messages", brokered.RunToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            customMessage: "the ORIGINAL base URL and the ORIGINAL bearer must BOTH still work. Mint a fresh port and this is a refused connection; mint a fresh route or a fresh token and it is a 401 — and either way the agent that is still running has no model");
        upstream.LastRequest!.Headers.GetValues("x-api-key").Single().ShouldBe(UpstreamKey,
            "a restored lease fronts the tenant's key server-side exactly as the original did — a re-bind that forwarded the run token instead would hand the provider a bearer it has no use for");
        workerB.HasLease(runId).ShouldBeTrue("the run is held HERE now, which is the answer a re-attach acts on before deciding the run can no longer reach a model");
        (await workerB.RenewAsync(runId, 8, CancellationToken.None)).ShouldBeTrue(
            "the restored lease is keyed on the RE-ATTACH's epoch, not the launch's — keyed on the old one the new worker's heartbeat renews nothing and the lease lapses two beats later");
    }

    [Fact]
    public async Task Rebind_reports_false_when_the_port_is_taken_rather_than_pretending_it_worked()
    {
        using var occupied = new OccupiedPort();
        var logger = new CapturingLogger();
        using var broker = LoopbackModelCredentialBroker.ForTest(new StubUpstream(), logger: logger);
        var request = RebindOn(occupied.Port, epoch: 8);

        // NEVER-THROWS is the load-bearing half, and it is the half that broke: the managed HttpListener reports a
        // taken port as an HttpListenerException on Windows/macOS but as a bare SocketException from Socket.Bind on
        // Linux, so an enumerated catch let Linux's escape — out of a re-attach prelude that runs BEFORE the
        // executor's own try, failing the whole re-attach and leaving the run Running with nobody observing it. Which
        // exception a given OS throws is exactly what a test cannot assume, so the contract pinned here is that none
        // of them gets out.
        bool? refused = null;
        await Should.NotThrowAsync(async () => refused = await broker.RebindAsync(request, CancellationToken.None));

        refused.ShouldBe(false,
            customMessage: "a re-bind onto a port something else holds must SAY so. A silent success leaves the caller believing the run's model access is back, so it lands no verdict and clears the posture that says otherwise — a run Running forever with an agent that cannot talk, which is the exact degrade the typed landing exists to remove");

        broker.HasLease(request.RunId).ShouldBeFalse(
            "a failed re-bind must install NOTHING: a lease with no listener behind it would make HasLease lie to the one caller deciding whether the run can still reach a model");

        logger.Warnings.ShouldContain(line => line.Contains(occupied.Port.ToString(), StringComparison.Ordinal) && line.Contains(request.RunId.ToString(), StringComparison.Ordinal),
            "the refusal has to name the run and the port, or an operator cannot tell a transient conflict from a handle this worker was never going to restore");
    }

    /// <summary>
    /// A port genuinely unavailable to the broker — held by RAW sockets, bound and listening, on EVERY address it
    /// would try (the wildcard first, then loopback).
    ///
    /// <para>Raw and listening is the shape that matters: it is a listening socket on the exact address that makes the
    /// managed <c>HttpListener</c> fail its <c>Socket.Bind</c>, which is where Linux raises the bare
    /// <c>SocketException</c> that an enumerated catch missed.</para>
    ///
    /// <para>And one socket is not enough — finding that out is the other half. With <c>SO_REUSEADDR</c>, which every
    /// .NET socket sets on Unix, binding <c>127.0.0.1:P</c> SUCCEEDS while something else holds <c>0.0.0.0:P</c>, so a
    /// fixture occupying only the wildcard would let the re-bind through and pass for the wrong reason. Only an EXACT
    /// duplicate of a listening socket is refused.</para>
    /// </summary>
    private sealed class OccupiedPort : IDisposable
    {
        private readonly Socket _wildcard = Listening(new IPEndPoint(IPAddress.Any, 0));
        private readonly Socket _loopback;

        public OccupiedPort()
        {
            Port = ((IPEndPoint)_wildcard.LocalEndPoint!).Port;
            _loopback = Listening(new IPEndPoint(IPAddress.Loopback, Port));
        }

        public int Port { get; }

        private static Socket Listening(IPEndPoint endpoint)
        {
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(endpoint);
            socket.Listen(1);

            return socket;
        }

        public void Dispose()
        {
            _loopback.Dispose();
            _wildcard.Dispose();
        }
    }

    [Fact]
    public async Task Rebind_refuses_to_displace_a_lease_this_worker_holds_at_a_newer_epoch()
    {
        var runId = Guid.NewGuid();
        using var broker = LoopbackModelCredentialBroker.ForTest(new StubUpstream());

        // The run's first attempt, then a re-claim: the second open supersedes the first and CLOSES its listener, so
        // the old port is free again. That freedom is what makes this test about the epoch and nothing else — a stale
        // re-bind that was merely losing a port conflict would be refused for a reason this guard does not own.
        var stale = await broker.OpenAsync(LeaseFor(runId, epoch: 7), CancellationToken.None);
        if (stale is null) return;
        var live = await broker.OpenAsync(LeaseFor(runId, epoch: 9), CancellationToken.None);

        live!.RebindPort.ShouldNotBe(stale.RebindPort, "precondition: the live attempt is on its own port, so the stale one's is bindable");

        // A pass from BEHIND the fence arrives holding the address the handle recorded at epoch 7. Honouring it would
        // install that lease over the live one and close the live attempt's listener — handing the run back to the
        // worker the fence already settled against, by a route the fence never sees.
        (await broker.RebindAsync(RebindOf(stale, runId, epoch: 7), CancellationToken.None)).ShouldBeFalse(
            customMessage: "a re-bind from behind the fence must be refused — the epoch is the only thing that separates restoring an address from taking one, and every other field a superseded worker presents is identical");

        (await CallAsync(live, "/v1/messages", live.RunToken)).StatusCode.ShouldBe(HttpStatusCode.OK,
            "the live attempt's own address must be untouched by the refusal; closing it would be the takeover, arrived at by a different route");
        (await broker.RenewAsync(runId, 9, CancellationToken.None)).ShouldBeTrue("and the lease the broker keys on must still be epoch 9's");
    }

    /// <summary>The re-bind a later worker would build from what a run's durable handle carries — the point being that every value comes from <paramref name="brokered"/>, because a re-bind restores an address and never mints one.</summary>
    private static ModelCredentialRebindRequest RebindOf(BrokeredModelCredential brokered, Guid runId, long epoch) => new()
    {
        RunId = runId, TeamId = Guid.NewGuid(), Epoch = epoch, Port = brokered.RebindPort!.Value, PathId = brokered.RebindRoute!,
        RunToken = brokered.RunToken, Upstream = AnthropicCredential(), Ttl = TimeSpan.FromMinutes(3),
    };

    /// <summary>A re-bind naming a port no lease of ours ever bound — for the cases where what is under test is the bind itself.</summary>
    private static ModelCredentialRebindRequest RebindOn(int port, long epoch) => new()
    {
        RunId = Guid.NewGuid(), TeamId = Guid.NewGuid(), Epoch = epoch, Port = port, PathId = "a-recorded-route-id",
        RunToken = "a-recorded-run-token-long-enough-to-be-one", Upstream = AnthropicCredential(), Ttl = TimeSpan.FromMinutes(3),
    };

    // ── What the re-attach checks BEFORE it asks the broker ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true, true, true)]       // the handle names an address a worker can bind
    [InlineData(false, true, true, false)]     // no port — a handle from before the address was recorded
    [InlineData(true, false, true, false)]     // no route — half an address is no address
    [InlineData(true, true, false, false)]     // no bearer — an unbrokered run has nothing to restore
    public void Only_a_handle_that_records_the_whole_address_is_rebindable(bool port, bool route, bool token, bool rebindable) =>
        AgentRunExecutor.IsRebindable(new SandboxHandle
        {
            Kind = "local", ProcessId = 1, SpoolDirectory = "/tmp", Deadline = DateTimeOffset.UtcNow,
            ModelBrokerPort = port ? 44444 : null, ModelBrokerRoute = route ? "route-id" : null, ModelBrokerRunToken = token ? "run-token" : null,
        }).ShouldBe(rebindable,
            customMessage: "a partial address is not an address: binding a port with no route, or installing a route with no bearer, produces a lease the detached agent's own base URL cannot use — and the caller would then clear the posture that says the run has no model");

    [Theory]
    [InlineData("Anthropic", "Anthropic", true, true, true)]      // same row, same provider → the same credential
    [InlineData("Anthropic", "anthropic", true, true, true)]      // hosts spell their own tags; the tag is not case
    [InlineData("Anthropic", "OpenAI", true, true, false)]        // a different provider is a different API, not a different key
    [InlineData("Anthropic", "Anthropic", true, false, false)]    // the resolve landed on another row — a rotation, or a changed team default
    [InlineData(null, "Anthropic", false, false, false)]          // the launch recorded no provider at all (an unbrokered handle)
    [InlineData("Anthropic", "Anthropic", false, false, true)]    // both row-less: the operator-global key, which has no row to name
    public void A_rebind_only_fronts_the_credential_the_launch_itself_fronted(string? stampedProvider, string resolvedProvider, bool stampedRow, bool sameRow, bool fronts)
    {
        var stamped = Guid.NewGuid();
        var handle = new SandboxHandle
        {
            Kind = "local", ProcessId = 1, SpoolDirectory = "/tmp", Deadline = DateTimeOffset.UtcNow,
            ModelBrokerProvider = stampedProvider, ModelBrokerCredentialId = stampedRow ? stamped : null,
        };
        var resolved = new ResolvedModelCredential { Provider = resolvedProvider, CredentialId = stampedRow && sameRow ? stamped : stampedRow ? Guid.NewGuid() : null };

        AgentRunExecutor.FrontsTheSameCredential(handle, resolved).ShouldBe(fronts,
            customMessage: "a re-attach re-resolves the credential from scratch and can legitimately land somewhere else. Re-binding that would spend a key the run's posture never recorded under a bearer minted for a different one — and a changed PROVIDER is worse still, because the relay's upstream root and path allowlist both come from it, so the child would be talking a wire its new upstream does not serve");
    }

    [Fact]
    public void A_handle_written_before_the_broker_address_existed_deserializes_and_is_not_rebindable()
    {
        // Byte-for-byte the shape a worker on the previous build persisted: a bearer, and no address beside it. It has
        // to READ — a re-attach that threw here could not recover the run at all — and it has to answer "not
        // rebindable", which is what puts a mixed-version deploy's in-flight runs back on the typed landing instead of
        // onto a port nobody wrote down.
        const string legacy = """
        {"kind":"local","processId":4242,"spoolDirectory":"/tmp/spool","deadline":"2026-01-01T00:00:00+00:00","modelBrokerRunToken":"a-bearer-from-the-previous-build"}
        """;

        var handle = JsonSerializer.Deserialize<SandboxHandle>(legacy, AgentJson.Options).ShouldNotBeNull();

        handle.ModelBrokerRunToken.ShouldBe("a-bearer-from-the-previous-build", "the one broker field the old build did write must survive — the re-attach rebuilds its redactor from it");
        handle.ModelBrokerPort.ShouldBeNull();
        handle.ModelBrokerRoute.ShouldBeNull();
        handle.ModelBrokerCredentialId.ShouldBeNull();
        handle.ModelBrokerProvider.ShouldBeNull();

        AgentRunExecutor.IsRebindable(handle).ShouldBeFalse(
            "the mixed-version deploy story in one line: old handles keep the outcome they always had, and only handles that recorded an address survive a restart");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<LoopbackModelCredentialBroker>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    // ── The upstream address ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Anthropic", null, "https://api.anthropic.com")]
    [InlineData("OpenAI", null, "https://api.openai.com")]
    [InlineData("Anthropic", "https://gw.example.com/v1", "https://gw.example.com")]
    [InlineData("OpenAI", "https://gw.example.com/v1/", "https://gw.example.com")]
    [InlineData("OpenRouter", "https://openrouter.ai/api/v1", "https://openrouter.ai/api")]
    [InlineData("Custom", "https://local.example", "https://local.example")]
    [InlineData("SomethingNobodyPinned", null, null)]
    public void The_upstream_root_is_the_credentials_own_endpoint_or_the_providers_default(string provider, string? baseUrl, string? expected) =>
        LoopbackModelCredentialBroker.UpstreamRootFor(new() { Provider = provider, BaseUrl = baseUrl, ApiKey = UpstreamKey }).ShouldBe(expected,
            customMessage: "the version segment comes from the CLI's own request path, so the root must carry neither a second /v1 nor a trailing slash — and an unknown provider with no base URL has no endpoint to forward to at all");

    [Theory]
    [InlineData("/route/v1/messages", "https://api.anthropic.com/v1/messages")]
    [InlineData("/route/v1/responses", "https://api.anthropic.com/v1/responses")]
    [InlineData("/route", "https://api.anthropic.com/")]
    public void A_proxied_path_is_appended_to_the_upstream_root_verbatim(string requestedPath, string expected) =>
        LoopbackModelCredentialBroker.UpstreamUriFor("https://api.anthropic.com", "route", new Uri("http://127.0.0.1:9/" + requestedPath.TrimStart('/') + "?beta=true"))
            .ToString().ShouldBe(expected + "?beta=true");

    // ── The path allowlist ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/v1/messages", HttpStatusCode.OK)]
    [InlineData("/v1/messages/count_tokens", HttpStatusCode.OK)]
    [InlineData("/v1/files", HttpStatusCode.Forbidden)]
    [InlineData("/v1/organizations/me", HttpStatusCode.Forbidden)]
    public async Task Only_the_providers_own_model_paths_are_relayed(string path, HttpStatusCode expected)
    {
        var upstream = new StubUpstream();
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);

        var brokered = await broker.OpenAsync(LeaseFor(Guid.NewGuid()), CancellationToken.None);
        if (brokered is null) return;

        (await CallAsync(brokered, path, brokered.RunToken)).StatusCode.ShouldBe(expected,
            customMessage: "the run's bearer is a capability on the provider's MODEL API, not on every endpoint the tenant's key opens — without the allowlist the relay attaches that key to whatever path a compromised CLI appends");

        upstream.Calls.ShouldBe(expected == HttpStatusCode.OK ? 1 : 0,
            "a refused path must be refused HERE: a call relayed and then rejected upstream has already spent the tenant's key on it");
    }

    [Theory]
    [InlineData("Anthropic", "/v1/messages", true)]
    [InlineData("Anthropic", "/v1/messages/", true)]        // a trailing slash names the same endpoint
    [InlineData("Anthropic", "/v1/responses", false)]       // the OTHER provider's wire is not this one's surface
    [InlineData("Anthropic", "", false)]                    // a bare route is not a model call
    [InlineData("OpenAI", "/v1/responses", true)]
    [InlineData("OpenAI", "/v1/chat/completions", true)]
    [InlineData("OpenAI", "/v1/models", true)]
    [InlineData("OpenAI", "/v1/files", false)]
    [InlineData("OpenRouter", "/v1/responses", true)]        // OpenRouter shares OpenAI's table: Codex drives it through the same Responses-wire override
    [InlineData("OpenRouter", "/v1/models", true)]
    [InlineData("OpenRouter", "/v1/files", false)]
    [InlineData("Ollama", "/anything/at/all", true)]         // a real, named provider with no table row → unrestricted: an operator gateway's path surface is theirs to define
    [InlineData(null, "/v1/files", true)]
    public void The_relay_path_allowlist_is_per_provider_and_silent_for_a_provider_it_does_not_name(string? provider, string path, bool relayable) =>
        LoopbackModelCredentialBroker.IsRelayablePath(provider, path).ShouldBe(relayable,
            customMessage: "the allowlist narrows the two APIs this codebase drives and refuses to guess at the rest — refusing an unenumerable gateway's paths would break the run instead of narrowing it");

    // ── The window, and the fail-closed decision ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_projected_base_url_leaves_the_reachable_host_for_the_runner_to_fill_in()
    {
        using var broker = LoopbackModelCredentialBroker.ForTest(new StubUpstream());

        var brokered = await broker.OpenAsync(LeaseFor(Guid.NewGuid()), CancellationToken.None);
        if (brokered is null) return;

        brokered.BaseUrl.ShouldStartWith($"http://{SandboxSpec.ModelBrokerHostToken}:",
            customMessage: "the host must stay a token until launch: a run inside a filtered-egress netns reaches this worker at ITS OWN gateway address, not on loopback, and that address is only reserved during the launch");
        brokered.RunToken.Length.ShouldBeGreaterThan(32, "the bearer IS the capability — it has to be unguessable, not merely unique");
    }

    [Fact]
    public void The_lease_window_is_two_heartbeats()
    {
        ModelCredentialLease.TtlHeartbeats.ShouldBe(2);
        ModelCredentialLease.Ttl.ShouldBe(AgentRunLiveness.HeartbeatInterval * 2,
            customMessage: "the lease window is defined against the cadence that renews it — pinning the ratio is what stops a heartbeat-interval change from silently making every lease expire mid-run (too short) or letting a dead worker's agent keep spending (too long)");
        ModelCredentialLease.Ttl.ShouldBeLessThan(AgentRunLiveness.Window,
            customMessage: "a lease must lapse BEFORE the reconciler would even call the run stale, or an abandoned run can still spend the key while nothing has declared it abandoned");
    }

    [Theory]
    [InlineData(true, false, true, true)]     // required, a key would land, unbrokered → refuse
    [InlineData(true, true, true, false)]     // required and brokered → proceed
    [InlineData(true, false, false, false)]   // required but no credential to inject at all → nothing to refuse
    [InlineData(false, false, true, false)]   // not required → the key is injected AND disclosed, never refused
    [InlineData(false, false, false, false)]
    public void An_unbrokerable_credential_is_refused_only_where_the_deployment_requires_confinement(bool required, bool brokered, bool wouldInject, bool refuses)
    {
        var act = () => ModelCredentialBrokerage.EnsureSatisfiable(wouldInject, brokered, required);

        if (refuses)
            Should.Throw<ModelCredentialBrokerUnavailableException>(act).Message.ShouldContain("Sandbox:RequireConfinement",
                customMessage: "the refusal must name the setting an operator would change, or it is unactionable");
        else
            Should.NotThrow(act);
    }

    [Theory]
    [InlineData(true, false, false)]   // a key landed unbrokered → the record says so, and the posture line discloses it
    [InlineData(true, true, true)]     // brokered → recorded as brokered, nothing to disclose
    [InlineData(false, false, null)]   // no credential reached the sandbox at all → say nothing about one
    public void The_recorded_posture_says_nothing_when_no_credential_reached_the_sandbox(bool wouldInject, bool brokered, bool? expected) =>
        ModelCredentialBrokerage.BrokeredPosture(wouldInject, brokered).ShouldBe(expected,
            customMessage: "a credential-less run recording 'not brokered' would make the journal disclose a direct injection that never happened");

    [Fact]
    public void The_run_redactor_strikes_the_brokered_bearer()
    {
        var brokered = new BrokeredModelCredential("http://127.0.0.1:1/r", "run-token-long-enough-to-be-a-needle", DateTimeOffset.UtcNow);

        var redactor = AgentRunExecutor.WithModelBrokerRunToken(AgentRunExecutor.BuildRunRedactor(new Dictionary<string, string>(), AnthropicCredential()), brokered.RunToken);

        redactor.Redact($"401 unauthorized for bearer {brokered.RunToken}").Contains(brokered.RunToken, StringComparison.Ordinal).ShouldBeFalse(
            "the bearer rides the child's env, so a CLI that echoes its environment or a 401 body writes it into the append-only log — which cannot be edited afterwards");
        redactor.Redact($"and the key was {UpstreamKey}").Contains(UpstreamKey, StringComparison.Ordinal).ShouldBeFalse(
            "the upstream key stays a needle under brokerage — the broker holds it, and a broker error can echo it");
    }

    [Fact]
    public void A_lost_lease_lands_a_declared_code_whose_words_never_blame_the_provider()
    {
        var result = AgentRunExecutor.AsLostModelAccess(new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit" });

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe(FailureCodes.ModelCredentialLeaseLost);
        result.ExitReason.ShouldBe("model_credential_lease_lost", "the exit reason is read by the lane classifier and by the retry verdict — renaming it is a wire break, not a refactor");

        FailureCodes.All.ShouldContain(FailureCodes.ModelCredentialLeaseLost,
            "RealModelRunClassifier.IsGatewayInfra reserves every code in this set as OUR fault; outside it, a run this codebase deliberately ended would be re-read as a gateway skip and let a real gate go green");

        // The words are the change. What it replaced was a run that went on failing to connect until its spec
        // timeout — indistinguishable, to the first reader, from the provider being down.
        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("worker", Case.Insensitive, "the cause has to name the worker restart, or the reader is left to guess");
        error.ShouldContain("Retry", Case.Insensitive, "an operator-facing terminal that does not say what to do next is a dead end");
        error.ShouldNotContain("provider", Case.Insensitive, "the provider is fine; sending a reader to check one costs them the hour this sentence exists to save");
        error.ShouldNotContain("gateway", Case.Insensitive, "same reason — a gateway-shaped word here is exactly the misdiagnosis this outcome removes");
    }

    [Fact]
    public void The_lost_lease_verdict_never_overwrites_a_run_that_actually_succeeded()
    {
        // A kill races the agent's own exit. One that finished between the probe and the signal has a REAL success on
        // the spool, and stamping this verdict over it would destroy completed work and make an operator retry
        // something already done.
        var succeeded = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "did the thing", SessionId = "sess-1" };

        AgentRunExecutor.AsLostModelAccess(succeeded).ShouldBeSameAs(succeeded, "a finished attempt is not an attempt that could not finish");
    }

    [Fact]
    public void The_lost_lease_verdict_keeps_everything_the_attempt_produced()
    {
        var folded = new AgentRunResult
        {
            Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Summary = "got partway",
            SessionId = "sess-resumable", TokenUsage = new AgentTokenUsage { InputTokens = 1200, OutputTokens = 340 },
        };

        var landed = AgentRunExecutor.AsLostModelAccess(folded);

        landed.SessionId.ShouldBe("sess-resumable", "the session id is what makes the retry WARM rather than a cold re-run of work already paid for");
        landed.TokenUsage!.InputTokens.ShouldBe(1200);
        landed.Summary.ShouldBe("got partway");
        landed.ExitReason.ShouldBe(FailureCodes.ModelCredentialLeaseLost, "only the three fields that say WHAT HAPPENED are replaced");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The base URL as the RUNNER would hand it to a child: the projected URL carries
    /// <see cref="SandboxSpec.ModelBrokerHostToken"/> (only the launch knows which address a given child can reach
    /// the worker at), and the runner substitutes it. The test does the same substitution rather than assuming a
    /// literal loopback URL, so a change that hardcoded one — breaking every filtered-egress run — goes red in
    /// <see cref="The_projected_base_url_leaves_the_reachable_host_for_the_runner_to_fill_in"/>.
    /// </summary>
    private static string Reachable(BrokeredModelCredential brokered) =>
        brokered.BaseUrl.Replace(SandboxSpec.ModelBrokerHostToken, "127.0.0.1", StringComparison.Ordinal);

    /// <summary>The api-key carrier a Claude Code run presents its bearer on (the auth-token one is a plain <c>Authorization: Bearer</c>).</summary>
    private const string ApiKeyCarrier = "x-api-key";

    private static HttpRequestMessage Authorized(BrokeredModelCredential brokered, string path, string? token, string carrier = "Authorization")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Reachable(brokered) + path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

        if (token is not null) request.Headers.TryAddWithoutValidation(carrier, ApiKeyCarrier.Equals(carrier, StringComparison.OrdinalIgnoreCase) ? token : $"Bearer {token}");

        return request;
    }

    private static async Task<HttpResponseMessage> CallAsync(BrokeredModelCredential brokered, string path, string? token, string carrier = "Authorization")
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        return await client.SendAsync(Authorized(brokered, path, token, carrier));
    }

    /// <summary>
    /// Whether a call on this bearer is REFUSED. Now that every lease owns its own port, a withdrawal presents two ways
    /// depending on timing: a 401 while something is still bound to the address, and no answer at all once the listener
    /// is closed. Both say the identical thing — this bearer buys no model spend — and pinning only the 401 would make
    /// the STRONGER withdrawal (the address ceasing to exist) read as a regression. What never varies, and what every
    /// caller asserts beside this, is that the provider saw nothing.
    /// </summary>
    private static async Task<bool> RefusedAsync(BrokeredModelCredential brokered, string token)
    {
        try { return (await CallAsync(brokered, "/v1/messages", token)).StatusCode == HttpStatusCode.Unauthorized; }
        catch (HttpRequestException) { return true; }   // nothing is bound there any more
    }

    /// <summary>
    /// The provider, stubbed at the message-handler seam so the request it RECEIVES can be asserted exactly. Not a
    /// second listener: what matters is the upstream headers and URI, and a handler sees them without a second hop.
    /// </summary>
    private sealed class StubUpstream : HttpMessageHandler
    {
        private readonly ProducerStream _stream = new();

        public bool Streamed { get; init; }
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public List<string> SeenHeaderValues { get; } = [];

        public void Push(string text) => _stream.Push(Encoding.UTF8.GetBytes(text));

        public void Complete() => _stream.Complete();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            foreach (var header in request.Headers) SeenHeaderValues.AddRange(header.Value);

            if (!Streamed) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") });

            var content = new StreamContent(_stream);
            content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        protected override void Dispose(bool disposing)
        {
            _stream.Complete();
            base.Dispose(disposing);
        }
    }

    /// <summary>A response body the test pushes chunks into: a read waits for the next pushed chunk, so ordering — and therefore chunk-by-chunk delivery — is deterministic rather than raced against a buffer size.</summary>
    private sealed class ProducerStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _chunks = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        public void Push(byte[] chunk) => _chunks.Writer.TryWrite(chunk);

        public void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (!_chunks.Reader.TryRead(out var chunk)) return 0;

            chunk.CopyTo(buffer.Span);

            return chunk.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
