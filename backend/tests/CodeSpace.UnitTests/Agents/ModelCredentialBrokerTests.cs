using System.Net;
using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Credentials;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Agents;
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

    [Fact]
    public async Task A_live_lease_relays_the_call_with_the_tenants_key_attached_server_side()
    {
        var upstream = new StubUpstream();
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var runId = Guid.NewGuid();

        var brokered = await broker.OpenAsync(LeaseFor(runId), CancellationToken.None);
        if (brokered is null) return;   // this host cannot bind a loopback listener at all — nothing to assert

        var response = await CallAsync(brokered, "/v1/messages", brokered.RunToken);

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

        var refused = await CallAsync(brokered, "/v1/messages", brokered.RunToken);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            customMessage: "a revoked lease must refuse the NEXT call — revocation that only takes effect when the process dies is the defect this slice exists to remove");
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
        (await CallAsync(first, "/v1/messages", first.RunToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            customMessage: "the superseded attempt's bearer must stop working the moment the run is re-claimed");
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

    private static HttpRequestMessage Authorized(BrokeredModelCredential brokered, string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Reachable(brokered) + path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

        if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        return request;
    }

    private static async Task<HttpResponseMessage> CallAsync(BrokeredModelCredential brokered, string path, string? token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        return await client.SendAsync(Authorized(brokered, path, token));
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
