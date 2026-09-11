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
/// The in-process broker: ONE HTTP reverse proxy per worker, on an ephemeral port, with a per-run route and a per-run
/// bearer. A brokered run's CLI talks to <c>http://&lt;worker&gt;:&lt;port&gt;/&lt;run path id&gt;/…</c> and the
/// proxy replays the request upstream with the tenant's real key attached SERVER-SIDE. The key exists only in the
/// lease object; the sandbox holds a token that authenticates here and nowhere else.
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
    private readonly ConcurrentDictionary<string, Lease> _byRoute = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ILogger<LoopbackModelCredentialBroker> _logger;
    private readonly TimeProvider _time;
    private readonly HttpClient _upstream;
    private readonly object _startLock = new();

    private HttpListener? _listener;
    private string? _baseUrlTemplate;
    private bool _startFailed;

    public LoopbackModelCredentialBroker(ILogger<LoopbackModelCredentialBroker>? logger = null, TimeProvider? timeProvider = null)
        : this(logger, timeProvider, null) { }

    /// <summary>
    /// Test seam: a broker whose UPSTREAM transport is the test's, so it can assert the request the PROVIDER receives
    /// (the tenant's key attached, the run token absent) rather than infer it. A factory rather than a wider public
    /// constructor deliberately — a bare <see cref="HttpMessageHandler"/> registration appearing in the container
    /// later must not silently become the path every tenant's model traffic takes.
    /// </summary>
    internal static LoopbackModelCredentialBroker ForTest(HttpMessageHandler upstream, TimeProvider? timeProvider = null) => new(null, timeProvider, upstream);

    private LoopbackModelCredentialBroker(ILogger<LoopbackModelCredentialBroker>? logger, TimeProvider? timeProvider, HttpMessageHandler? upstreamHandler)
    {
        _logger = logger ?? NullLogger<LoopbackModelCredentialBroker>.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _upstream = new HttpClient(upstreamHandler ?? DefaultUpstreamHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>No automatic decompression and no redirect following: the proxy copies BYTES, so an upstream that gzips is passed through with its own <c>Content-Encoding</c> and the client decompresses, exactly as it would talking to the provider directly.</summary>
    private static HttpMessageHandler DefaultUpstreamHandler() =>
        new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, AllowAutoRedirect = false, ConnectTimeout = UpstreamConnectTimeout };

    public Task<BrokeredModelCredential?> OpenAsync(ModelCredentialLeaseRequest request, CancellationToken cancellationToken)
    {
        if (UpstreamRootFor(request.Upstream) is not { } upstreamRoot) return Task.FromResult<BrokeredModelCredential?>(null);
        if (EnsureListening() is not { } template) return Task.FromResult<BrokeredModelCredential?>(null);

        var lease = new Lease(request.RunId, request.TeamId, request.Epoch, McpRunToken.Mint(), McpRunToken.MintPathId(), request.Upstream, upstreamRoot);
        lease.RenewUntil(_time.GetUtcNow() + request.Ttl);

        // A second open for the same run REPLACES the previous lease and un-routes its token at once — that is the
        // reclaimed-run case (a new attempt at a higher epoch), where the superseded worker's token must stop being
        // honoured immediately rather than at the end of a TTL it could still be renewing.
        if (_byRun.TryGetValue(request.RunId, out var superseded)) _byRoute.TryRemove(superseded.PathId, out _);

        _byRun[request.RunId] = lease;
        _byRoute[lease.PathId] = lease;

        _logger.LogDebug("Model credential brokered for agent run {RunId} (team {TeamId}, epoch {Epoch}) until {ExpiresAt:O}", lease.RunId, lease.TeamId, lease.Epoch, lease.ExpiresAt);

        return Task.FromResult<BrokeredModelCredential?>(new(template.Replace(RoutePlaceholder, lease.PathId, StringComparison.Ordinal), lease.Token, lease.ExpiresAt));
    }

    public Task<bool> RenewAsync(Guid runId, long epoch, CancellationToken cancellationToken)
    {
        if (!_byRun.TryGetValue(runId, out var lease) || lease.Epoch != epoch) return Task.FromResult(false);

        lease.RenewUntil(_time.GetUtcNow() + ModelCredentialLease.Ttl);

        return Task.FromResult(true);
    }

    public Task RevokeAsync(Guid runId, string reason, CancellationToken cancellationToken)
    {
        if (!_byRun.TryRemove(runId, out var lease)) return Task.CompletedTask;

        _byRoute.TryRemove(lease.PathId, out _);

        _logger.LogInformation("Model credential lease revoked for agent run {RunId}: {Reason}", runId, reason);

        return Task.CompletedTask;
    }

    /// <summary>Test seam: whether this run currently holds a live lease here. Not on the interface — nothing in production asks, and a caller that did would be reasoning about another worker's memory.</summary>
    internal bool HasLease(Guid runId) => _byRun.TryGetValue(runId, out var lease) && lease.ExpiresAt > _time.GetUtcNow();

    // ── Listener ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The route segment the base-URL template carries until a lease substitutes its own id in. Never reaches a child.</summary>
    private const string RoutePlaceholder = "{route}";

    /// <summary>
    /// The base-URL template for this worker's listener, starting it on first use. Null once a start attempt has
    /// FAILED — recorded so every later run takes the caller's decision path immediately instead of re-probing ports
    /// on a host that cannot listen at all.
    /// </summary>
    private string? EnsureListening()
    {
        lock (_startLock)
        {
            if (_listener is not null) return _baseUrlTemplate;
            if (_startFailed) return null;

            if (Bind() is not { } bound)
            {
                _startFailed = true;
                _logger.LogWarning("The model-credential broker could not bind a listener on this worker; runs will fall back to whatever their deployment's confinement policy permits");
                return null;
            }

            _listener = bound.Listener;
            _baseUrlTemplate = $"http://{SandboxSpec.ModelBrokerHostToken}:{bound.Port}/{RoutePlaceholder}";
            _ = Task.Run(() => AcceptAsync(bound.Listener), CancellationToken.None);

            _logger.LogInformation("Model-credential broker listening on port {Port} (bound to {Host})", bound.Port, bound.Host);

            // A host that CAN build per-run network namespaces but refused the wide bind can broker only its
            // shared-network runs: a sealed run's child reaches this worker at its namespace gateway, and nothing is
            // listening there. Said out loud because the failure it produces is a model call that times out, which
            // reads like a provider problem rather than a bind that fell back.
            if (FilteredEgressNetns.IsSupported && bound.Host != AnyHost)
                _logger.LogWarning("The model-credential broker fell back to a loopback-only bind on a host that builds filtered-egress namespaces; a deny-by-default egress run cannot reach it there");

            return _baseUrlTemplate;
        }
    }

    /// <summary>
    /// Bind the listener: every address on a host that can build filtered-egress namespaces (their children reach the
    /// worker on a per-run gateway IP, not on loopback), loopback otherwise. The wider bind is TRIED FIRST and falls
    /// back, so a host that refuses it still brokers its shared-network runs.
    /// </summary>
    private static (HttpListener Listener, int Port, string Host)? Bind()
    {
        foreach (var host in CandidateHosts())
            for (var attempt = 0; attempt < BindAttempts; attempt++)
            {
                var port = ReserveEphemeralPort();

                if (TryBind(host, port) is { } listener) return (listener, port, host);
            }

        return null;
    }

    /// <summary>The <see cref="HttpListener"/> prefix host that binds every address AND matches any Host header — what a per-run namespace's child necessarily sends, since it addresses the worker by its gateway IP.</summary>
    private const string AnyHost = "+";

    private const string LoopbackHost = "127.0.0.1";

    /// <summary>Bind addresses in order of preference — see <see cref="Bind"/>. Every address first only where a per-run network namespace can exist to need it.</summary>
    private static IReadOnlyList<string> CandidateHosts() =>
        FilteredEgressNetns.IsSupported ? new[] { AnyHost, LoopbackHost } : new[] { LoopbackHost };

    private static HttpListener? TryBind(string host, int port)
    {
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add($"http://{host}:{port}/");
            listener.Start();
            return listener;
        }
        catch (Exception exception) when (exception is HttpListenerException or PlatformNotSupportedException or ObjectDisposedException or ArgumentException)
        {
            listener.Close();
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

    private async Task AcceptAsync(HttpListener listener)
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }

            _ = Task.Run(() => ServeQuietlyAsync(context), CancellationToken.None);
        }
    }

    // ── One request ───────────────────────────────────────────────────────────────────────────────────────────────

    private async Task ServeQuietlyAsync(HttpListenerContext context)
    {
        try
        {
            if (Authorize(context.Request) is not { } lease) { Refuse(context.Response, HttpStatusCode.Unauthorized); return; }
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
    /// The run whose lease this request may spend, or null — which is a 401 and nothing more informative. Every
    /// refusal reason (unknown route, wrong/absent token, expired lease, revoked lease, a source that cannot be one
    /// of our sandboxes) collapses to the same answer on purpose: a caller probing the port learns only that it has
    /// no capability here.
    /// </summary>
    private Lease? Authorize(HttpListenerRequest request)
    {
        if (RouteOf(request.Url) is not { } route || !_byRoute.TryGetValue(route, out var lease)) return null;
        if (PresentedToken(request) is not { } presented || !McpRunToken.Matches(lease.Token, presented)) return null;
        if (lease.ExpiresAt <= _time.GetUtcNow()) return null;
        if (!IsPlausibleSandboxSource(request.RemoteEndPoint?.Address)) return null;

        return lease;
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

        _byRun.Clear();
        _byRoute.Clear();

        try { _listener?.Close(); } catch (Exception exception) when (exception is ObjectDisposedException or HttpListenerException) { /* best-effort */ }

        _upstream.Dispose();
        _stopping.Dispose();
    }

    /// <summary>
    /// One run's live brokerage. The decrypted key sits HERE and nowhere else — not on the run row, not in the spec,
    /// not in the child's environment — so the process holding it is the same one whose heartbeat keeps the lease
    /// alive. <see cref="ExpiresAt"/> is renewed from another thread than the one reading it, hence the interlocked
    /// tick field rather than a property a race could tear.
    /// </summary>
    private sealed class Lease(Guid runId, Guid teamId, long epoch, string token, string pathId, ResolvedModelCredential upstream, string upstreamRoot)
    {
        private long _expiresAtUtcTicks;

        public Guid RunId { get; } = runId;
        public Guid TeamId { get; } = teamId;
        public long Epoch { get; } = epoch;
        public string Token { get; } = token;
        public string PathId { get; } = pathId;
        public ResolvedModelCredential Upstream { get; } = upstream;
        public string UpstreamRoot { get; } = upstreamRoot;

        public DateTimeOffset ExpiresAt => new(Interlocked.Read(ref _expiresAtUtcTicks), TimeSpan.Zero);

        public void RenewUntil(DateTimeOffset instant) => Interlocked.Exchange(ref _expiresAtUtcTicks, instant.UtcDateTime.Ticks);
    }
}
