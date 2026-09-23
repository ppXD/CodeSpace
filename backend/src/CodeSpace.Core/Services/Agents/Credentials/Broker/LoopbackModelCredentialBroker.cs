using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSpace.Core.Services.Agents.Credentials.Broker;

/// <summary>
/// The in-process broker: an HTTP reverse proxy PER RUN, each on its own ephemeral port, with a per-run route and a
/// per-run bearer. A brokered run's CLI talks to <c>http://&lt;worker&gt;:&lt;port&gt;/&lt;run path id&gt;/…</c> and
/// the proxy replays the request upstream with the tenant's real key attached SERVER-SIDE. The key exists only in the
/// lease object; the sandbox holds a token that authenticates here and nowhere else.
///
/// <para><b>Why a listener per lease rather than one per worker.</b> The address a run is given is frozen into a
/// detached CLI's configuration at launch, so it is the only address that run will ever call — which makes re-opening
/// it after a worker restart the difference between a deploy interrupting a run and a deploy ENDING it
/// (<see cref="RebindAsync"/>). A shared worker port could be re-bound too, but it would make every run's address one
/// address: a single conflict would take out every in-flight run on the host, and two workers on one host could never
/// both broker. A port per lease fails one run at a time, and the cost is one idle <see cref="HttpListener"/> per
/// concurrent agent run.</para>
///
/// <para><b>Withdrawal closes the port.</b> A revoked or superseded lease has its listener closed, not merely
/// un-routed: the capability's ADDRESS stops existing rather than answering 401 until the process ends. So a call on a
/// withdrawn bearer presents either way depending on timing — refused while something is still bound, unanswered once
/// it is not — and both say the same thing, which is that the bearer buys nothing. Nothing is relayed either way.</para>
///
/// <para><b>Where it listens, and why it cannot simply be loopback.</b> A deny-by-default egress run executes inside
/// a per-run network namespace, so <c>127.0.0.1</c> there is the NAMESPACE's loopback — a broker bound only to the
/// host's would be unreachable by exactly the runs that most need one. The namespace does reach the worker: its
/// default route is the veth gateway (<c>FilteredEgressPlan</c>'s <c>.1</c>), and a packet addressed to the host's
/// own address is delivered locally (INPUT) rather than FORWARDED, so the run's nftables allowlist — a forward-hook
/// filter — never sees it and no allowlist entry is needed. But that /30 is reserved DURING the launch, after this
/// lease was opened and its base URL projected, so the listener cannot be bound to it up front: on a host that can
/// build such namespaces it binds every address (<c>http://+:port/</c>, which also matches any Host header) and the
/// runner substitutes <see cref="SandboxSpec.ModelBrokerHostToken"/> with the address that particular child can
/// reach. On a host that cannot (macOS development, a container with no <c>ip</c>/<c>nft</c>) it stays on loopback,
/// where nothing needs the wider bind.</para>
///
/// <para><b>What guards it.</b> The 256-bit bearer, checked in constant time, is the capability; the run's route
/// segment is a 128-bit CSPRNG id, so a caller cannot even find another run's route by holding its id; the lease
/// expires without renewal and is withdrawn outright on cancel. The source-address gate below is defence in depth,
/// not the guarantee: it admits loopback and the <c>10.0.0.0/8</c> space the subnet allocator hands out, so a
/// deployment whose pods sit in a 10/8 network has neighbours that can REACH the port — and still cannot use it
/// without a live run's token.</para>
///
/// <para><b>Streaming is load-bearing.</b> Both harnesses stream (SSE), so the proxy reads response headers only
/// (<see cref="HttpCompletionOption.ResponseHeadersRead"/>) and flushes every chunk it copies. A buffered relay would
/// turn a live token stream into one late blob and break the CLIs' own progress reporting.</para>
/// </summary>
public sealed class LoopbackModelCredentialBroker : IModelCredentialBroker, IDisposable, ISingletonDependency
{
    /// <summary>Bind attempts before giving up. Each takes a FRESH ephemeral port, because the only realistic failure is losing the race between probing a free port and binding it.</summary>
    private const int BindAttempts = 3;

    private const int CopyBufferBytes = 16 * 1024;

    /// <summary>How long the upstream has to ACCEPT a connection. The response itself is unbounded (an SSE stream legitimately stays open for the length of a turn), so the run's own wall clock and stall watchdog remain its bounds.</summary>
    private static readonly TimeSpan UpstreamConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Request headers the proxy never replays: hop-by-hop, the ones the HTTP stack owns, and the two the run token arrives on (replaced with the upstream key, never forwarded).</summary>
    private static readonly HashSet<string> DroppedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "keep-alive", "proxy-connection", "transfer-encoding", "upgrade", "te", "trailer", "content-length", "expect", "authorization", "x-api-key",
    };

    /// <summary>Response headers the proxy never copies verbatim: hop-by-hop, the framing ones it sets itself from the upstream response's own shape, and the ones the listener owns. Anything else it cannot set is skipped rather than failing the relay (see <see cref="CopyResponseHead"/>).</summary>
    private static readonly HashSet<string> DroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "transfer-encoding", "upgrade", "trailer", "content-length", "content-type", "date", "server",
    };

    /// <summary>The provider tag whose API authenticates by <c>x-api-key</c>. Every other endpoint this codebase drives takes a bearer — the same split <c>ClaudeCodeHarness.ProjectToEnv</c> already encodes (api key for Anthropic itself, auth token for a gateway).</summary>
    private const string ApiKeyHeaderProvider = "Anthropic";

    /// <summary>The paths the relay carries to an ANTHROPIC upstream: the Messages API the Claude Code CLI drives, plus its token-count sibling. Public so the surface a run's key can be spent on is pinnable, and so widening it is a reviewed edit rather than a side effect of a path-handling change.</summary>
    public static readonly IReadOnlySet<string> AnthropicRelayPaths = new HashSet<string>(StringComparer.Ordinal) { "/v1/messages", "/v1/messages/count_tokens" };

    /// <summary>The paths the relay carries to an OPENAI upstream: the Responses wire Codex drives, the Chat Completions wire an OpenAI-compatible gateway serves, and the model listing a CLI reads at start-up.</summary>
    public static readonly IReadOnlySet<string> OpenAiRelayPaths = new HashSet<string>(StringComparer.Ordinal) { "/v1/responses", "/v1/chat/completions", "/v1/models" };

    /// <summary>
    /// The per-provider path allowlist, indexed by the credential's provider tag (the same spelling
    /// <see cref="EgressAllowlistBuilder.ProviderDefaultHosts"/> keys on). A run's token is a capability on ONE
    /// provider API, and without this the relay would spend the tenant's key on any path a compromised CLI appended
    /// — file uploads, batches, an account-management endpoint the key also opens.
    ///
    /// <para><c>OpenRouter</c> shares <see cref="OpenAiRelayPaths"/> rather than getting its own row: the only
    /// harness that can carry an OpenRouter credential is <c>CodexHarness</c>
    /// (<c>ClaudeCodeHarness.SupportedProviders</c> has no OpenRouter entry), and Codex drives every non-default
    /// provider — OpenRouter included — through the SAME <c>AppendModelProviderConfig</c> Responses-wire override it
    /// uses for a Custom OpenAI-compatible gateway. The relayed path this proxy ever sees is therefore
    /// <c>/v1/responses</c> (or <c>/v1/models</c> at CLI start-up) regardless of which upstream host the credential
    /// resolves to — OpenRouter's own REST shape (<c>/api/v1/chat/completions</c>) never reaches the relay, because
    /// <see cref="NormalizeUpstreamRoot"/> already folds the <c>/api</c> segment into the upstream ROOT rather than
    /// the per-request path. A provider NOT named here relays unrestricted, deliberately: an operator gateway's path
    /// surface is theirs, not ours to enumerate, and refusing what we cannot enumerate would break the run rather
    /// than narrow it. The bearer + the lease remain the guarantee for those; this table narrows the APIs we do
    /// know the shape of.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RelayPathsByProvider = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["Anthropic"] = AnthropicRelayPaths,
        ["OpenAI"] = OpenAiRelayPaths,
        ["OpenRouter"] = OpenAiRelayPaths,
    };

    private readonly ConcurrentDictionary<Guid, Lease> _byRun = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly ILogger<LoopbackModelCredentialBroker> _logger;
    private readonly TimeProvider _time;
    private readonly HttpClient _upstream;

    /// <summary>Reclaims the sockets of leases nobody withdrew — see <see cref="SweepLapsedLeases"/>. Disposed with the broker, so a torn-down worker leaves no callback behind.</summary>
    private readonly ITimer _sweep;

    public LoopbackModelCredentialBroker(ILogger<LoopbackModelCredentialBroker>? logger = null, TimeProvider? timeProvider = null)
        : this(logger, timeProvider, null) { }

    /// <summary>
    /// Test seam: a broker whose UPSTREAM transport is the test's, so it can assert the request the PROVIDER receives
    /// (the tenant's key attached, the run token absent) rather than infer it. A factory rather than a wider public
    /// constructor deliberately — a bare <see cref="HttpMessageHandler"/> registration appearing in the container
    /// later must not silently become the path every tenant's model traffic takes.
    /// </summary>
    internal static LoopbackModelCredentialBroker ForTest(HttpMessageHandler upstream, TimeProvider? timeProvider = null, ILogger<LoopbackModelCredentialBroker>? logger = null) => new(logger, timeProvider, upstream);

    /// <summary>
    /// Test seam: kill the listener behind a LIVE lease without going through a revoke — the platform failure this
    /// class cannot otherwise be made to have, and the one whose handling
    /// (<see cref="DropIfStillServing"/>) is the difference between a lease that stops being claimed and a
    /// <see cref="HasLease"/> that lies forever. A single narrow method rather than a wider surface: nothing in
    /// production calls it, and the behaviour it triggers has no other trigger.
    /// </summary>
    internal void BreakListenerForTest(Guid runId)
    {
        if (_byRun.TryGetValue(runId, out var lease)) CloseQuietly(lease.Listener);
    }

    private LoopbackModelCredentialBroker(ILogger<LoopbackModelCredentialBroker>? logger, TimeProvider? timeProvider, HttpMessageHandler? upstreamHandler)
    {
        _logger = logger ?? NullLogger<LoopbackModelCredentialBroker>.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _upstream = new HttpClient(upstreamHandler ?? DefaultUpstreamHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _sweep = _time.CreateTimer(_ => SweepLapsedLeases(), null, SweepInterval, SweepInterval);
    }

    /// <summary>No automatic decompression and no redirect following: the proxy copies BYTES, so an upstream that gzips is passed through with its own <c>Content-Encoding</c> and the client decompresses, exactly as it would talking to the provider directly.</summary>
    private static HttpMessageHandler DefaultUpstreamHandler() =>
        new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, AllowAutoRedirect = false, ConnectTimeout = UpstreamConnectTimeout };

    public Task<BrokeredModelCredential?> OpenAsync(ModelCredentialLeaseRequest request, CancellationToken cancellationToken)
    {
        if (UpstreamRootFor(request.Upstream) is not { } upstreamRoot) return Task.FromResult<BrokeredModelCredential?>(null);

        if (BindFresh(out var bindFailure) is not { } bound)
        {
            _logger.LogWarning(bindFailure, "Agent run {RunId}: the model-credential broker could not bind a listener on this worker after {Attempts} fresh ports, so the run falls back to whatever its deployment's confinement policy permits", request.RunId, BindAttempts);
            return Task.FromResult<BrokeredModelCredential?>(null);
        }

        var lease = Install(new Lease
        {
            RunId = request.RunId, TeamId = request.TeamId, Epoch = request.Epoch, Token = McpRunToken.Mint(), PathId = McpRunToken.MintPathId(),
            Upstream = request.Upstream, UpstreamRoot = upstreamRoot, Listener = bound.Listener, Port = bound.Port,
        }, request.Ttl);

        _logger.LogDebug("Model credential brokered for agent run {RunId} on port {Port} (team {TeamId}, epoch {Epoch}) until {ExpiresAt:O}", lease.RunId, lease.Port, lease.TeamId, lease.Epoch, lease.ExpiresAt);
        WarnIfUnreachableFromNetns(lease, bound.Host);

        return Task.FromResult<BrokeredModelCredential?>(new(BaseUrlFor(lease), lease.Token, lease.ExpiresAt) { RebindPort = lease.Port, RebindRoute = lease.PathId });
    }

    /// <summary>
    /// Re-open a run's recorded address — see <see cref="IModelCredentialBroker.RebindAsync"/> for why nothing here is
    /// minted. Every refusal is a false plus one Warning naming the reason: the caller's alternative is ending a live
    /// run typed, so "the port is taken" and "the credential named no endpoint" must not reach an operator as the same
    /// silence.
    /// </summary>
    public Task<bool> RebindAsync(ModelCredentialRebindRequest request, CancellationToken cancellationToken)
    {
        if (request.Port is <= 0 or > MaxPort) return RefuseRebind(request, "the recorded port is not a bindable TCP port");
        if (UpstreamRootFor(request.Upstream) is not { } upstreamRoot) return RefuseRebind(request, "the credential names no upstream endpoint to forward to");

        if (_byRun.TryGetValue(request.RunId, out var held))
        {
            // A lease held here at a NEWER epoch is a live claim by a later attempt. Replacing it would close a port
            // that attempt's own agent is calling and hand the run back to a superseded worker — the exact reclaim
            // this fence exists to settle, decided backwards.
            if (held.Epoch > request.Epoch) return RefuseRebind(request, $"a lease at a newer epoch ({held.Epoch}) is already held on this worker");

            // THE SAME ADDRESS, already up, on this very worker: a re-attach of a run this process never stopped
            // serving (a reclaim after the observation lease lapsed while the old pass was between heartbeats). There
            // is nothing to re-open, and trying is actively destructive — BindPort would walk the candidate hosts
            // against a port THIS PROCESS holds, and on Linux the wide bind fails while the loopback one succeeds
            // underneath it, so Install would close the live WIDE listener and leave a sealed netns run talking to an
            // address it cannot reach, with true returned and the posture cleared. Adopt it instead.
            if (IsSameAddress(held, request)) return AdoptHeldLease(held, request);
        }

        if (BindPort(request.Port, out var bindFailure) is not { } bound) return RefuseRebind(request, "its port could not be bound here — something else is holding it, or this host refused the bind", bindFailure);

        var lease = Install(new Lease
        {
            RunId = request.RunId, TeamId = request.TeamId, Epoch = request.Epoch, Token = request.RunToken, PathId = request.PathId,
            Upstream = request.Upstream, UpstreamRoot = upstreamRoot, Listener = bound.Listener, Port = bound.Port,
        }, request.Ttl);

        _logger.LogInformation("Model credential RE-BOUND for agent run {RunId} on port {Port} (team {TeamId}, epoch {Epoch}) until {ExpiresAt:O}; its detached agent's next model call is answered here", lease.RunId, lease.Port, lease.TeamId, lease.Epoch, lease.ExpiresAt);
        WarnIfUnreachableFromNetns(lease, bound.Host);

        return Task.FromResult(true);
    }

    /// <summary>Whether a lease this worker already holds IS the address the request is asking for — same port, same route, same bearer. All three, because any one of them differing means the child would be talking to something other than what the handle recorded.</summary>
    private static bool IsSameAddress(Lease held, ModelCredentialRebindRequest request) =>
        held.Port == request.Port && string.Equals(held.PathId, request.PathId, StringComparison.Ordinal) && McpRunToken.Matches(held.Token, request.RunToken);

    /// <summary>
    /// Take over a lease this worker is ALREADY serving at the re-attach's epoch, without touching its listener. The
    /// socket stays exactly as it was bound — which is the point, since re-binding it is what would move a wide bind
    /// down to loopback — and only the two things a new claimant owns change: the fence the lease answers renewals on,
    /// and its window.
    ///
    /// <para>The epoch move is what makes the adoption real rather than cosmetic: <see cref="RenewAsync"/> is fenced,
    /// so a lease left at the previous attempt's epoch would refuse every heartbeat the re-attach sends and lapse two
    /// beats later — the run would lose its model access anyway, just more slowly and for a reason nothing logs.</para>
    /// </summary>
    private Task<bool> AdoptHeldLease(Lease held, ModelCredentialRebindRequest request)
    {
        held.AdoptEpoch(request.Epoch);
        held.RenewUntil(_time.GetUtcNow() + request.Ttl);

        _logger.LogInformation("Model credential lease ADOPTED for agent run {RunId} on port {Port} (team {TeamId}) at epoch {Epoch}: this worker already serves the address the handle records, so nothing was re-bound", held.RunId, held.Port, held.TeamId, request.Epoch);

        return Task.FromResult(true);
    }

    /// <summary>Say WHY a re-bind could not take, and answer false. Warning rather than Debug because the consequence of the false is a live run ended typed, and this line is the only place that separates a transient port conflict from a handle this worker was never going to be able to restore. The bind's own exception rides along when there was one — its type is what tells a port conflict from a host that refuses the prefix, and the two want different operator responses.</summary>
    private Task<bool> RefuseRebind(ModelCredentialRebindRequest request, string reason, Exception? failure = null)
    {
        _logger.LogWarning(failure, "The model-credential broker could not re-bind agent run {RunId} on port {Port}: {Reason}", request.RunId, request.Port, reason);

        return Task.FromResult(false);
    }

    /// <summary>
    /// Make a lease live: set its window, REPLACE any lease the run already held — closing that listener, so a
    /// superseded attempt's address stops existing at once rather than at the end of a TTL its own worker could still
    /// be renewing — and start serving the new one.
    /// </summary>
    private Lease Install(Lease lease, TimeSpan ttl)
    {
        lease.RenewUntil(_time.GetUtcNow() + ttl);

        // PUBLISH first, close second. The superseded lease's accept loop wakes the moment its listener closes and
        // asks whether it is still the run's current lease; installing the replacement first means the answer is
        // always no, so it exits quietly instead of racing to remove an entry this method is about to overwrite.
        var superseded = _byRun.TryGetValue(lease.RunId, out var previous) ? previous : null;

        _byRun[lease.RunId] = lease;

        if (superseded is not null) CloseQuietly(superseded.Listener);

        // CALLED, not handed to Task.Run: an async method runs synchronously up to its first await, so the loop's first
        // wait is registered on the listener before this returns. The managed HttpListener fails only the waits it
        // already holds when it closes; a close that lands while a pool thread is still registering the first one is
        // never delivered, and that loop then waits for the life of the worker on a listener that no longer exists.
        _ = AcceptAsync(lease);

        return lease;
    }

    public Task<bool> RenewAsync(Guid runId, long epoch, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        if (!_byRun.TryGetValue(runId, out var lease) || lease.Epoch != epoch) return Task.FromResult(false);

        // A lease that has ALREADY lapsed is not renewed, it is refused. Two reasons, and the second is a race.
        //
        // The honest one: an expired lease already reads false from HasLease and is already refused at the relay, so
        // renewing it would resurrect a capability that was, for some interval, dead — while leaving the three
        // answers disagreeing about the same instant. The window is not tight enough for that to be an accident: the
        // TTL is TWO heartbeat intervals, so a single missed ping never reaches here and only a worker that has gone
        // quiet for two consecutive beats does.
        //
        // The race: the sweep below reads an expiry, decides to reclaim, and then removes and closes. A renewal
        // landing inside that window would otherwise extend a lease whose socket is about to be closed anyway —
        // leaving the table claiming a live lease that nothing serves. Refusing here means ExpiresAt can never move
        // forward once passed, so the sweep's decision cannot be invalidated after it is taken. Fixing it on this
        // side rather than by re-inserting after the removal, because a re-insert races an OPEN for the same run and
        // would then close the listener that open just installed.
        if (lease.ExpiresAt <= now) return Task.FromResult(false);

        lease.RenewUntil(now + ModelCredentialLease.Ttl);

        return Task.FromResult(true);
    }

    public Task RevokeAsync(Guid runId, string reason, long? fencedToEpoch, CancellationToken cancellationToken)
    {
        if (!_byRun.TryGetValue(runId, out var lease)) return Task.CompletedTask;

        // A FENCED revoke is one a pass issues about its OWN attempt on its way out. If the lease has moved on to a
        // later epoch, that attempt is not the one holding the address any more — a same-process re-attach adopted it
        // — and withdrawing it here would take a live run's model access away on the way out of a pass that no longer
        // owns the run. An UNFENCED revoke (a cancel, an abandon) is a statement about the RUN rather than about one
        // attempt, and means it at every epoch.
        if (fencedToEpoch is { } epoch && lease.Epoch != epoch)
        {
            _logger.LogInformation("Model credential lease for agent run {RunId} left alone on {Reason}: it is held at epoch {HeldEpoch}, not the epoch {Epoch} this pass owned, so a later claimant is serving it", runId, reason, lease.Epoch, epoch);
            return Task.CompletedTask;
        }

        if (!_byRun.TryRemove(new KeyValuePair<Guid, Lease>(runId, lease))) return Task.CompletedTask;   // it moved between the read and here; its new owner's revoke will close it

        // The listener goes with the lease, not merely the routing entry: leaving it bound would hold one port per
        // finished run for the life of the worker, and a worker serves thousands.
        CloseQuietly(lease.Listener);

        _logger.LogInformation("Model credential lease revoked for agent run {RunId}: {Reason}", runId, reason);

        return Task.CompletedTask;
    }

    /// <summary>Whether this run currently holds a live lease HERE — see <see cref="IModelCredentialBroker.HasLease"/> for why a caller is allowed to ask. An entry past its expiry answers false without waiting for the sweep that reclaims it: a lapsed lease is already refused at the relay, and the two must not disagree.</summary>
    public bool HasLease(Guid runId) => _byRun.TryGetValue(runId, out var lease) && lease.ExpiresAt > _time.GetUtcNow();

    // ── Reclaiming what nobody withdrew ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How often lapsed leases are reclaimed. NOT a correctness bound — an expired lease is already refused at the
    /// relay and already reads false from <see cref="HasLease"/>, both the instant it lapses — so this only decides
    /// how long a dead lease's SOCKET is held, which makes a round minute the right kind of arbitrary.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Close the listener of every lease whose window has passed. Defence in depth behind the callers that revoke:
    /// each of them is a path that can be missed (a pass that crashes between its work and its finally, a future
    /// entry point nobody wires a revoke into), and what a miss leaks is not a dictionary entry but a bound PORT and
    /// a live accept task for the life of the worker. Runs on <see cref="TimeProvider"/>'s timer so a test can drive
    /// it rather than sleep through it.
    /// </summary>
    private void SweepLapsedLeases()
    {
        var now = _time.GetUtcNow();

        foreach (var (runId, lease) in _byRun)
        {
            // Nothing may escape a timer callback: an unhandled exception on a TimeProvider timer takes the PROCESS
            // down, which would turn a socket-reclaiming nicety into the worst outage this class could cause. The only
            // throw left in the body is a misbehaving logger, and the catch costs nothing.
            try
            {
                if (lease.ExpiresAt > now || !_byRun.TryRemove(new KeyValuePair<Guid, Lease>(runId, lease))) continue;

                CloseQuietly(lease.Listener);

                _logger.LogInformation("Model credential lease for agent run {RunId} lapsed without being withdrawn; its port {Port} is reclaimed", runId, lease.Port);
            }
            catch (Exception) { /* this lease keeps its socket until the next tick; the sweep is best-effort by design */ }
        }
    }

    // ── Listener ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The highest bindable TCP port — the bound a RESTORED port is checked against, since it arrives from a persisted row rather than from this process's own allocator.</summary>
    private const int MaxPort = 65535;

    /// <summary>The address a lease's child is handed: this run's own port and route, with the reachable host left as a token for the runner to substitute at launch (see <see cref="SandboxSpec.ModelBrokerHostToken"/>).</summary>
    private static string BaseUrlFor(Lease lease) => $"http://{SandboxSpec.ModelBrokerHostToken}:{lease.Port}/{lease.PathId}";

    /// <summary>
    /// A host that CAN build per-run network namespaces but refused the wide bind can serve only its shared-network
    /// runs: a sealed run's child reaches this worker at its namespace gateway, and nothing is listening there. Said
    /// out loud because the failure it produces is a model call that times out, which reads like a provider problem
    /// rather than a bind that fell back.
    /// </summary>
    private void WarnIfUnreachableFromNetns(Lease lease, string host)
    {
        if (!FilteredEgressNetns.IsSupported || host == AnyHost) return;

        _logger.LogWarning("Agent run {RunId}: its model-credential broker fell back to a loopback-only bind on a host that builds filtered-egress namespaces; a deny-by-default egress run cannot reach it there", lease.RunId);
    }

    /// <summary>
    /// Bind a FRESH ephemeral port for a new lease: every address on a host that can build filtered-egress namespaces
    /// (their children reach the worker on a per-run gateway IP, not on loopback), loopback otherwise. The wider bind
    /// is TRIED FIRST and falls back, so a host that refuses it still brokers its shared-network runs.
    /// </summary>
    private static (HttpListener Listener, int Port, string Host)? BindFresh(out Exception? failure)
    {
        failure = null;

        foreach (var host in CandidateHosts())
            for (var attempt = 0; attempt < BindAttempts; attempt++)
            {
                var port = ReserveEphemeralPort();

                if (TryBind(host, port, out failure) is { } listener) return (listener, port, host);
            }

        return null;
    }

    /// <summary>
    /// Bind ONE GIVEN port — a re-bind's whole job. No fresh-port retry, deliberately: the address is not this
    /// process's to choose, it is the one a detached agent already holds, so a substitute would answer nobody. The
    /// same candidate hosts as <see cref="BindFresh"/>, because the run whose port this is was launched on a host of
    /// the same shape and its child reaches the worker the same way.
    /// </summary>
    private static (HttpListener Listener, int Port, string Host)? BindPort(int port, out Exception? failure)
    {
        failure = null;

        foreach (var host in CandidateHosts())
            if (TryBind(host, port, out failure) is { } listener) return (listener, port, host);

        return null;
    }

    /// <summary>The <see cref="HttpListener"/> prefix host that binds every address AND matches any Host header — what a per-run namespace's child necessarily sends, since it addresses the worker by its gateway IP.</summary>
    private const string AnyHost = "+";

    private const string LoopbackHost = "127.0.0.1";

    /// <summary>Bind addresses in order of preference — see <see cref="BindFresh"/>. Every address first only where a per-run network namespace can exist to need it.</summary>
    private static IReadOnlyList<string> CandidateHosts() =>
        FilteredEgressNetns.IsSupported ? new[] { AnyHost, LoopbackHost } : new[] { LoopbackHost };

    /// <summary>
    /// Bind ONE prefix, or null plus the reason it could not. EVERY exception counts as "did not bind" — deliberately
    /// wider than an enumerated list, and the width is the fix.
    ///
    /// <para><b>The platform split that made an enumeration wrong.</b> A taken port surfaces as an
    /// <see cref="HttpListenerException"/> on the Windows/macOS paths and as a bare
    /// <see cref="SocketException"/> (<c>Address already in use</c>, thrown from <c>Socket.Bind</c> inside the managed
    /// listener's endpoint manager) on Linux. An enumeration that had not met Linux let that one escape — into a
    /// re-attach prelude whose two callers both document that they never throw, where it failed the whole re-attach
    /// and left the run Running with nobody observing it. The next platform to surface a new type is, by definition,
    /// the one nobody ran this on; a bind that threw is a bind that did not happen, whatever it threw.</para>
    ///
    /// <para>The reason travels out rather than being dropped, so the caller's Warning can name it: the difference
    /// between "something else holds this port" and "this host refuses the wide bind" is the difference between
    /// waiting and reconfiguring.</para>
    /// </summary>
    private static HttpListener? TryBind(string host, int port, out Exception? failure)
    {
        HttpListener? listener = null;
        failure = null;

        try
        {
            // The CONSTRUCTOR is inside the try too: it throws PlatformNotSupportedException where the HTTP stack is
            // unavailable, and that is the same class of escape as the bind itself — a run's re-attach must not fail
            // because this host has no listener to offer.
            listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{port}/");
            listener.Start();
            return listener;
        }
        catch (Exception exception)
        {
            failure = exception;

            if (listener is not null) CloseQuietly(listener);

            return null;
        }
    }

    /// <summary>A free port, learned by binding one and releasing it — <see cref="HttpListener"/> has no port-0 form, so the port must be known before its prefix is built. The narrow race (something else takes it in between) is what <see cref="BindAttempts"/> covers.</summary>
    private static int ReserveEphemeralPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally { probe.Stop(); }
    }

    /// <summary>Serve ONE lease's listener until it closes. Bound to the lease rather than to a shared table, so a request arriving on this run's port is answered against this run's lease and nothing else — a closed listener ends the loop, which is what makes revocation the end of an address rather than the end of a routing entry.</summary>
    private async Task AcceptAsync(Lease lease)
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            // ANY failure to accept means this listener is done, for the same reason TryBind catches everything: the
            // exception a given platform raises for a closed listener is not something to enumerate from one OS. It
            // matters more here than it used to — a revoke now closes a listener per FINISHED RUN, where before a
            // listener only ever closed at process teardown — and the only correct response to any of them is to stop
            // serving an address that no longer exists, and to stop CLAIMING it.
            try { context = await lease.Listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception exception) { DropIfStillServing(lease, exception); return; }

            _ = Task.Run(() => ServeQuietlyAsync(context, lease), CancellationToken.None);
        }
    }

    /// <summary>
    /// A lease whose accept loop has stopped must stop being CLAIMED, not just stop being served. The table entry is
    /// what <see cref="HasLease"/> answers from, and a true off a lease nothing accepts is the worst answer this class
    /// can give: the child's connections sit unaccepted in a backlog rather than being refused, and a re-attach reading
    /// true concludes the run still has model access — so it lands no verdict and leaves the run Running, with no
    /// model and no explanation, for as long as the worker lives.
    ///
    /// <para>Removed only while it is STILL this lease. A revoke or a supersede reached the table first in every
    /// ordinary case — that is WHY this loop woke — and dropping their replacement would withdraw a live run's
    /// address by way of tidying up after its predecessor.</para>
    /// </summary>
    private void DropIfStillServing(Lease lease, Exception? reason)
    {
        if (!_byRun.TryRemove(new KeyValuePair<Guid, Lease>(lease.RunId, lease))) return;   // already revoked or superseded — that path owns the close

        CloseQuietly(lease.Listener);

        if (_stopping.IsCancellationRequested) return;   // the broker is going away and every lease ends with it; none of that is news

        _logger.LogWarning(reason, "Agent run {RunId}: its brokered model listener on port {Port} stopped accepting, so the lease was dropped rather than left claiming an address nothing answers", lease.RunId, lease.Port);
    }

    // ── One request ───────────────────────────────────────────────────────────────────────────────────────────────

    private async Task ServeQuietlyAsync(HttpListenerContext context, Lease lease)
    {
        try
        {
            if (!Authorized(context.Request, lease)) { Refuse(context.Response, HttpStatusCode.Unauthorized); return; }
            if (UnrelayablePath(lease, context.Request.Url!) is { } refused) { RefusePath(context.Response, lease, refused); return; }

            await ForwardAsync(context, lease).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The message can carry the upstream host but never a key (the key is only ever a header this proxy sets).
            _logger.LogWarning(exception, "The model-credential broker could not relay a request upstream");
            Refuse(context.Response, HttpStatusCode.BadGateway);
        }
        finally { try { context.Response.Close(); } catch (Exception closing) when (closing is HttpListenerException or ObjectDisposedException or InvalidOperationException) { /* the client hung up mid-stream */ } }
    }

    /// <summary>
    /// Whether this request may spend THIS port's lease — false is a 401 and nothing more informative. Every refusal
    /// reason (another run's route, wrong/absent token, expired lease, a source that cannot be one of our sandboxes)
    /// collapses to the same answer on purpose: a caller probing the port learns only that it has no capability here.
    ///
    /// <para>The route is checked against the lease this listener SERVES rather than looked up in a table: with a port
    /// per run, a request carrying some other run's route is a request to the wrong address, and resolving it would
    /// make two ports interchangeable that the design keeps apart.</para>
    /// </summary>
    private bool Authorized(HttpListenerRequest request, Lease lease)
    {
        if (RouteOf(request.Url) is not { } route || !string.Equals(route, lease.PathId, StringComparison.Ordinal)) return false;
        if (PresentedToken(request) is not { } presented || !McpRunToken.Matches(lease.Token, presented)) return false;
        if (lease.ExpiresAt <= _time.GetUtcNow()) return false;

        return IsPlausibleSandboxSource(request.RemoteEndPoint?.Address);
    }

    /// <summary>
    /// The path this request wants relayed when its lease's provider does not permit that path, or null when it does.
    /// Separate from <see cref="Authorize"/> and answered with a DIFFERENT status on purpose: the 401s are all "you
    /// hold no capability here", while this is "the capability you hold does not reach that endpoint" — a distinction
    /// the run's own operator needs, since a 401 would send them hunting a credential problem.
    /// </summary>
    private static string? UnrelayablePath(Lease lease, Uri requested)
    {
        var path = RelayPathOf(lease.PathId, requested);

        return IsRelayablePath(lease.Upstream.Provider, path) ? null : path;
    }

    /// <summary>Whether the provider's allowlist admits this relay path. True for a provider the table does not name — see the remarks on <see cref="RelayPathsByProvider"/>.</summary>
    internal static bool IsRelayablePath(string? provider, string path)
    {
        if (provider is not { Length: > 0 } named || !RelayPathsByProvider.TryGetValue(named, out var allowed)) return true;

        return allowed.Contains(path.Length > 1 ? path.TrimEnd('/') : path);
    }

    private void RefusePath(HttpListenerResponse response, Lease lease, string path)
    {
        // Warning, not Debug: a false refusal here presents to the run as a hard model failure, and this line is the
        // only place that names the path to add to the allowlist.
        _logger.LogWarning("The model-credential broker refused to relay {Path} for agent run {RunId}: it is not on the {Provider} path allowlist", path, lease.RunId, lease.Upstream.Provider);

        Refuse(response, HttpStatusCode.Forbidden);
    }

    /// <summary>The first path segment — this run's unguessable route id. Null for a request with no segment at all.</summary>
    internal static string? RouteOf(Uri? url) =>
        url?.AbsolutePath.Trim('/').Split('/', 2)[0] is { Length: > 0 } segment ? segment : null;

    /// <summary>The bearer the CLI presented, from either carrier a harness projection can use: an <c>Authorization: Bearer</c> (Claude Code's auth-token shape, Codex's OpenAI shape) or an <c>x-api-key</c> (Claude Code's api-key shape).</summary>
    internal static string? PresentedToken(HttpListenerRequest request)
    {
        if (request.Headers["Authorization"] is { Length: > 0 } authorization)
            return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization["Bearer ".Length..].Trim() : authorization.Trim();

        return request.Headers["x-api-key"] is { Length: > 0 } apiKey ? apiKey.Trim() : null;
    }

    /// <summary>
    /// Whether a request's source could be one of this host's sandboxes: loopback (a run sharing the host network) or
    /// the <c>10.0.0.0/8</c> space <see cref="EgressSubnetAllocator"/> carves per-run /30s out of. Defence in depth
    /// behind the token — see the type remarks on what it does and does not buy.
    /// </summary>
    internal static bool IsPlausibleSandboxSource(IPAddress? source) =>
        source is not null && (IPAddress.IsLoopback(source) || (source.AddressFamily == AddressFamily.InterNetwork && source.GetAddressBytes()[0] == 10));

    private static void Refuse(HttpListenerResponse response, HttpStatusCode status)
    {
        try
        {
            response.StatusCode = (int)status;
            response.ContentLength64 = 0;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or HttpListenerException) { /* already answered / client gone */ }
    }

    private async Task ForwardAsync(HttpListenerContext context, Lease lease)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        using var request = BuildUpstreamRequest(context.Request, lease);
        using var response = await _upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

        CopyResponseHead(response, context.Response);

        await CopyBodyAsync(response, context.Response, linked.Token).ConfigureAwait(false);
    }

    private static HttpRequestMessage BuildUpstreamRequest(HttpListenerRequest source, Lease lease)
    {
        var request = new HttpRequestMessage(new HttpMethod(source.HttpMethod), UpstreamUriFor(lease.UpstreamRoot, lease.PathId, source.Url!));

        if (source.HasEntityBody) request.Content = BodyOf(source);

        CopyRequestHeaders(source, request);
        ApplyUpstreamAuth(request, lease.Upstream);

        return request;
    }

    /// <summary>
    /// The request body, carrying forward the length the CLIENT declared. <c>Content-Length</c> is dropped from the
    /// copied headers (the HTTP stack owns framing), so without restating it here a length-declared upload would be
    /// re-framed as chunked — and a gateway that refuses chunked request bodies answers 411, which the run reads as
    /// the model rejecting its prompt. Absent (a genuinely chunked client) it stays chunked, as it must.
    /// </summary>
    private static StreamContent BodyOf(HttpListenerRequest source)
    {
        var content = new StreamContent(source.InputStream);

        if (source.Headers["Content-Length"] is { Length: > 0 } && source.ContentLength64 >= 0) content.Headers.ContentLength = source.ContentLength64;

        return content;
    }

    private static void CopyRequestHeaders(HttpListenerRequest source, HttpRequestMessage request)
    {
        foreach (var name in source.Headers.AllKeys)
        {
            if (name is null || DroppedRequestHeaders.Contains(name)) continue;

            var values = source.Headers.GetValues(name) ?? Array.Empty<string>();

            // A content header (Content-Type and friends) is rejected by the request collection and belongs on the
            // body's, so the second attempt is the routing rule rather than a fallback.
            if (!request.Headers.TryAddWithoutValidation(name, values)) request.Content?.Headers.TryAddWithoutValidation(name, values);
        }
    }

    /// <summary>Attach the tenant's real key, in the shape the upstream expects. A keyless upstream (a local Ollama) gets no auth header — the broker still fronts it, so the sandbox never learns its address.</summary>
    private static void ApplyUpstreamAuth(HttpRequestMessage request, ResolvedModelCredential upstream)
    {
        if (upstream.ApiKey is not { Length: > 0 } key) return;

        if (string.Equals(upstream.Provider, ApiKeyHeaderProvider, StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("x-api-key", key);
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static void CopyResponseHead(HttpResponseMessage response, HttpListenerResponse target)
    {
        target.StatusCode = (int)response.StatusCode;

        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (DroppedResponseHeaders.Contains(name)) continue;

            foreach (var value in values) TryCopyHeader(target, name, value);
        }

        if (response.Content.Headers.ContentType is { } contentType) target.ContentType = contentType.ToString();

        // No declared length means a streamed body (SSE, chunked) — chunk it back out rather than buffering to
        // discover a length the client is not waiting for.
        if (response.Content.Headers.ContentLength is { } length) target.ContentLength64 = length;
        else target.SendChunked = true;
    }

    /// <summary>
    /// Copy one response header, SKIPPING one the listener refuses to set (a restricted name it computes itself, an
    /// upstream value it rejects as malformed). A header the relay cannot carry is a lost hint; a throw here would be
    /// a lost response — and the class of upstream reply that carries such a header is the 401 an operator most needs
    /// to see the body of.
    /// </summary>
    private static void TryCopyHeader(HttpListenerResponse target, string name, string value)
    {
        try { target.Headers.Add(name, value); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { /* not carryable — see remarks */ }
    }

    /// <summary>Copy the body through, flushing EVERY chunk: an SSE event that sits in a buffer is an event the CLI has not seen, and both harnesses drive their progress off exactly those.</summary>
    private static async Task CopyBodyAsync(HttpResponseMessage response, HttpListenerResponse target, CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[CopyBufferBytes];

        while (true)
        {
            var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0) return;

            await target.OutputStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            await target.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Upstream address ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The upstream URI for one proxied request: the lease's root, plus whatever the CLI asked for after the run's
    /// route segment, plus its query verbatim. Null-free by construction — the route segment is how the request was
    /// authorized, so it is a prefix of the path.
    /// </summary>
    internal static Uri UpstreamUriFor(string upstreamRoot, string route, Uri requested) => new(upstreamRoot + RelayPathOf(route, requested) + requested.Query);

    /// <summary>
    /// The path the CLI asked for BELOW this run's route segment — <c>/v1/messages</c> for
    /// <c>…/&lt;route&gt;/v1/messages</c>, and <c>""</c> for a bare route. The ONE unit both the allowlist gate and
    /// the upstream URI are computed from, so the path that passed the gate is provably the path relayed.
    /// </summary>
    internal static string RelayPathOf(string route, Uri requested)
    {
        var path = requested.AbsolutePath.TrimStart('/');

        return path.Length > route.Length ? path[route.Length..] : "";
    }

    /// <summary>
    /// The lease's upstream ROOT: the credential's own base URL when it has one, else the provider's default endpoint
    /// (reusing <see cref="EgressAllowlistBuilder.ProviderDefaultHosts"/>, so "the provider's own endpoint" has one
    /// definition here and in the egress allowlist). Null for an unknown provider with no base URL — there is then no
    /// endpoint to forward to, and declining is the honest answer.
    /// </summary>
    internal static string? UpstreamRootFor(ResolvedModelCredential credential)
    {
        if (credential.BaseUrl is { Length: > 0 } configured) return NormalizeUpstreamRoot(configured);

        return credential.Provider is { Length: > 0 } provider && EgressAllowlistBuilder.ProviderDefaultHosts.TryGetValue(provider, out var host)
            ? $"https://{host}"
            : null;
    }

    /// <summary>
    /// Strip a trailing slash and ONE trailing <c>/v1</c> from an upstream base URL, so the version segment comes
    /// from the CLI's own request path instead of being doubled. Both harnesses already normalize the same seam from
    /// the other side — <c>ClaudeCodeHarness.StripVersionSuffix</c> strips it because the Anthropic SDK appends
    /// <c>/v1/messages</c>, <c>CodexHarness.EnsureOpenAiVersionPath</c> adds it because Codex appends only
    /// <c>/responses</c> — so a request arriving here carries whichever version prefix its CLI believes in, and the
    /// root must not carry a second one. Idempotent: a root URL is returned unchanged. A non-<c>/v1</c> prefix
    /// (OpenRouter's <c>/api</c>) is preserved.
    /// </summary>
    internal static string NormalizeUpstreamRoot(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');

        return trimmed.EndsWith("/v1", StringComparison.Ordinal) ? trimmed[..^3] : trimmed;
    }

    public void Dispose()
    {
        try { _stopping.Cancel(); } catch (ObjectDisposedException) { /* already stopped */ }

        _sweep.Dispose();

        foreach (var lease in _byRun.Values) CloseQuietly(lease.Listener);

        _byRun.Clear();

        _upstream.Dispose();
        _stopping.Dispose();
    }

    /// <summary>Close one lease's listener, releasing its port. Best-effort against EVERY failure — the port being gone is the outcome this wanted, and it now runs on every revoke (once per finished run) and on every failed bind, so it is no place to discover which exception a platform raises for a listener that is already closed.</summary>
    private static void CloseQuietly(HttpListener listener)
    {
        try { listener.Close(); } catch (Exception) { /* already gone, or this platform says so differently */ }
    }

    /// <summary>
    /// One run's live brokerage. The decrypted key sits HERE and nowhere else — not on the run row, not in the spec,
    /// not in the child's environment — so the process holding it is the same one whose heartbeat keeps the lease
    /// alive. <see cref="ExpiresAt"/> is renewed from another thread than the one reading it, hence the interlocked
    /// tick field rather than a property a race could tear.
    ///
    /// <para>Init-only properties rather than a primary constructor: the nine values below include two bare numbers
    /// (the epoch and the port) that a positional list would let a caller swap without failing to compile, and the
    /// re-bind path is where such a swap would hand a live run the wrong address (Rule 1).</para>
    ///
    /// <para><b>A sealed CLASS, and it must stay one.</b> Three correctness sites remove a lease with
    /// <c>TryRemove(KeyValuePair)</c> — <see cref="RevokeAsync"/>, <see cref="SweepLapsedLeases"/> and
    /// <see cref="DropIfStillServing"/> — and every one of them depends on that comparison being REFERENCE equality,
    /// so each removes only the instance it read and never a replacement installed in between. Turning this into a
    /// <c>record</c> would silently switch it to value equality: two leases with identical fields would compare equal,
    /// and a revoke or a sweep could then close the listener of a lease it never looked at. Nothing would fail to
    /// compile.</para>
    /// </summary>
    private sealed class Lease
    {
        private long _expiresAtUtcTicks;

        public required Guid RunId { get; init; }
        public required Guid TeamId { get; init; }
        private long _epoch;

        /// <summary>The fence this lease answers renewals on. Settable in place (<see cref="AdoptEpoch"/>) because the accept loop and the relay hold THIS object: swapping in a replacement would leave them serving a lease nobody renews.</summary>
        public required long Epoch { get => Volatile.Read(ref _epoch); init => _epoch = value; }

        /// <summary>Move this lease to a new claimant's fence — see <see cref="AdoptHeldLease"/>.</summary>
        public void AdoptEpoch(long epoch) => Volatile.Write(ref _epoch, epoch);
        public required string Token { get; init; }
        public required string PathId { get; init; }
        public required ResolvedModelCredential Upstream { get; init; }
        public required string UpstreamRoot { get; init; }

        /// <summary>This lease's OWN listener — one per run, so the address can be withdrawn (and later restored) by itself.</summary>
        public required HttpListener Listener { get; init; }

        /// <summary>The port <see cref="Listener"/> is bound to. Handed back to the caller so it reaches the run's durable handle, which is the only place a later worker can learn the address from.</summary>
        public required int Port { get; init; }

        public DateTimeOffset ExpiresAt => new(Interlocked.Read(ref _expiresAtUtcTicks), TimeSpan.Zero);

        public void RenewUntil(DateTimeOffset instant) => Interlocked.Exchange(ref _expiresAtUtcTicks, instant.UtcDateTime.Ticks);
    }
}
