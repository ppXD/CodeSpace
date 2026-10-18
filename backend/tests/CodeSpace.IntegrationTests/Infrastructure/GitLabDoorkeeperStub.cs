using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>What a <see cref="GitLabDoorkeeperStub"/> saw and decided, one entry per request it answered.</summary>
public enum StubEvent
{
    /// <summary>A refresh token was presented once and rotated: the old pair is dead, a new one was issued.</summary>
    Rotated,
    /// <summary>A token request was refused: <c>invalid_grant</c> for a refresh token that is unknown, already used or revoked, or <c>invalid_client</c>.</summary>
    Refused,
    /// <summary>An authorization code was exchanged for a first pair.</summary>
    Authorized,
    /// <summary>A token was revoked at <c>/oauth/revoke</c>.</summary>
    Revoked,
    /// <summary>A REST call carried an OAuth access token that is live.</summary>
    Api200,
    /// <summary>A REST call carried nothing GitLab honours: no Bearer token, or one that is unknown, revoked or expired.</summary>
    Api401,
    /// <summary>A git smart-HTTP advertisement was served to a live OAuth access token.</summary>
    Git200,
    /// <summary>A git smart-HTTP request was challenged: no credentials, a dead token, or an OAuth token under a login other than <c>oauth2</c>.</summary>
    Git401,
    /// <summary>A request the stub has no route for.</summary>
    Unrouted
}

/// <summary>
/// A loopback stand-in for a GitLab whose OAuth server is Doorkeeper, for tests that need the provider to behave like
/// GitLab does when tokens are refreshed, burst or lost — not like a double that says yes.
///
/// <para><b>Token model.</b> Doorkeeper keeps an access token and its refresh token in ONE row. A refresh token is single
/// use: presenting the live one revokes that row — the access token dies with it, however much of its
/// <c>expires_in</c> is left — and issues a new row; presenting a row's refresh token again, or one that is unknown or
/// was revoked, is <c>invalid_grant</c>. GitLab revokes no wider family on that (gitlab-org/gitlab#364111), so the
/// pair a winner was issued survives a loser's refusal. A client whose id or secret does not match the issuing
/// application is refused with <c>invalid_client</c> or <c>invalid_grant</c>.</para>
///
/// <para><b>Endpoints.</b> <c>POST /oauth/token</c> (<c>refresh_token</c> and <c>authorization_code</c> grants; the code
/// <c>authz:&lt;user&gt;</c> issues <c>&lt;user&gt;</c> a first pair), <c>POST /oauth/revoke</c> (RFC 7009: 200 whatever
/// it was given), <c>GET /api/v4/user</c> and <c>GET /api/v4/projects/:id/members/all/:user_id</c> (Bearer only — GitLab
/// resolves an OAuth token from <c>Authorization: Bearer</c> and looks <c>PRIVATE-TOKEN</c> up as a personal access
/// token), any other <c>GET /api/v4/**</c> as an empty list, and git smart-HTTP <c>GET **/info/refs</c> for both services.
/// The git endpoint takes an OAuth access token as HTTP Basic with login <c>oauth2</c> — what GitLab through 17.x
/// requires of an OAuth token — or as <c>Authorization: Bearer</c>; anything else is a 401 with the
/// <c>WWW-Authenticate: Basic</c> challenge that makes git ask again. A live token gets an empty repository's
/// advertisement, which real git accepts for <c>ls-remote</c>.</para>
///
/// <para><b>Counters.</b> <see cref="Count"/> reads how often each <see cref="StubEvent"/> happened; <see cref="Log"/>
/// keeps them in order with the token involved, so a test can say which pair a refusal presented.</para>
///
/// <para>The OS picks the port; requests are served concurrently.</para>
/// </summary>
public sealed class GitLabDoorkeeperStub : IDisposable
{
    /// <summary>The OAuth application every stub knows. Pair these with the <see cref="Seed"/> calls' <c>app</c> argument.</summary>
    public const string DefaultClientId = "app1";
    public const string DefaultClientSecret = "secret1";

    private const int MaxBindAttempts = 5;
    private const string InvalidGrantBody = """{"error":"invalid_grant","error_description":"The provided authorization grant is invalid, expired, revoked, does not match the redirection URI used in the authorization request, or was issued to another client."}""";
    private const string InvalidClientBody = """{"error":"invalid_client","error_description":"Client authentication failed due to unknown client, no client authentication included, or unsupported authentication method."}""";
    private const string UnauthorizedBody = """{"message":"401 Unauthorized"}""";

    private readonly HttpListener _listener;
    private readonly object _gate = new();
    private readonly List<TokenRow> _rows = new();
    private readonly List<StubLogEntry> _log = new();
    private readonly Dictionary<string, string> _applications = new() { [DefaultClientId] = DefaultClientSecret };
    private int _sequence;

    public GitLabDoorkeeperStub()
    {
        (_listener, BaseUrl) = ListenOnFreeLoopbackPort();

        _ = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    /// <summary>The lifetime of every token issued from now on. GitLab's is 7200; a minute puts each token inside a refresher's two-minute buffer, so every resolve rotates.</summary>
    public int ExpiresInSeconds { get; set; } = 7200;

    /// <summary>The refresh is committed — the old pair is dead — and its answer is sent only after this long: a slow GitLab, or a slow network between it and the caller.</summary>
    public TimeSpan RotateReplyDelay { get; set; } = TimeSpan.Zero;

    /// <summary>The <c>access_level</c> the members endpoint reports. 30 is Developer, the lowest role CodeSpace counts as a member.</summary>
    public int MemberAccessLevel { get; set; } = 30;

    public IReadOnlyList<StubLogEntry> Log { get { lock (_gate) { return _log.ToList(); } } }

    public IReadOnlyList<TokenRow> Rows { get { lock (_gate) { return _rows.Select(r => r.Copy()).ToList(); } } }

    public int Count(StubEvent kind)
    {
        lock (_gate) { return _log.Count(l => l.Kind == kind); }
    }

    /// <summary>The refresh tokens each refusal presented, in order — which pair a loser still held.</summary>
    public IReadOnlyList<string> RefusedRefreshTokens()
    {
        lock (_gate) { return _log.Where(l => l.Kind == StubEvent.Refused && l.Token != null).Select(l => l.Token!).ToList(); }
    }

    /// <summary>Lets another OAuth application authenticate here, for a test that edits a provider instance's client.</summary>
    public void AddApplication(string clientId, string clientSecret)
    {
        lock (_gate) { _applications[clientId] = clientSecret; }
    }

    /// <summary>A pair issued before the test began: what the credential row held when the story starts.</summary>
    public TokenRow Seed(string app, string user, string accessToken, string refreshToken)
    {
        lock (_gate) { return NewRow(app, user, accessToken, refreshToken).Copy(); }
    }

    public bool IsAccessTokenLive(string accessToken)
    {
        lock (_gate) { return FindLiveByAccessToken(accessToken) != null; }
    }

    public bool IsRefreshTokenLive(string refreshToken)
    {
        lock (_gate) { return _rows.Any(r => r.RefreshToken == refreshToken && r.RevokedAt == null); }
    }

    /// <summary>The refresh token GitLab would accept right now for <paramref name="user"/> — what a credential row must hold to keep working.</summary>
    public string? LiveRefreshTokenOf(string user)
    {
        lock (_gate) { return _rows.LastOrDefault(r => r.User == user && r.RevokedAt == null)?.RefreshToken; }
    }

    public void Dispose()
    {
        try { _listener.Close(); }
        catch (ObjectDisposedException) { }
    }

    // ─── Serving ───────────────────────────────────────────────────────────────

    private async Task ServeAsync()
    {
        while (true)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;   // the listener was closed — the only way out of GetContextAsync
            }

            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        try
        {
            var body = await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync().ConfigureAwait(false);
            var reply = Route(context.Request, body);

            if (reply.Delay > TimeSpan.Zero) await Task.Delay(reply.Delay).ConfigureAwait(false);

            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = reply.ContentType;
            foreach (var (name, value) in reply.Headers) context.Response.Headers[name] = value;
            context.Response.ContentLength64 = reply.Body.Length;
            await context.Response.OutputStream.WriteAsync(reply.Body).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception)
        {
            try { context.Response.Abort(); }
            catch (Exception) { /* the caller went away, or the listener closed */ }
        }
    }

    private Reply Route(HttpListenerRequest request, string body)
    {
        var path = request.Url!.AbsolutePath;

        if (request.HttpMethod == "POST" && path == "/oauth/token") return Token(HttpUtility.ParseQueryString(body));
        if (request.HttpMethod == "POST" && path == "/oauth/revoke") return Revoke(HttpUtility.ParseQueryString(body));
        if (request.HttpMethod == "GET" && path.StartsWith("/api/v4/", StringComparison.Ordinal)) return Api(request.Headers["Authorization"], path);
        if (request.HttpMethod == "GET" && path.EndsWith("/info/refs", StringComparison.Ordinal)) return GitAdvertisement(request.Headers["Authorization"], HttpUtility.ParseQueryString(request.Url.Query)["service"]);

        Note(StubEvent.Unrouted, $"{request.HttpMethod} {request.Url.PathAndQuery}", null);

        return Reply.Json(501, """{"message":"no route in GitLabDoorkeeperStub"}""");
    }

    // ─── OAuth ─────────────────────────────────────────────────────────────────

    private Reply Token(System.Collections.Specialized.NameValueCollection form)
    {
        var clientId = form["client_id"] ?? string.Empty;
        var secret = form["client_secret"] ?? string.Empty;

        lock (_gate)
        {
            if (!_applications.TryGetValue(clientId, out var expected) || expected != secret)
            {
                Note(StubEvent.Refused, $"invalid_client client_id={clientId}", null);
                return Reply.Json(401, InvalidClientBody);
            }

            return form["grant_type"] switch
            {
                "refresh_token" => Refresh(clientId, form["refresh_token"] ?? string.Empty),
                "authorization_code" => Authorize(clientId, form["code"] ?? string.Empty),
                _ => Reply.Json(400, """{"error":"unsupported_grant_type"}""")
            };
        }
    }

    /// <summary>Doorkeeper's <c>refresh_token.with_lock { revoke the old row; create the new one }</c>. Runs under the stub's lock.</summary>
    private Reply Refresh(string clientId, string presented)
    {
        var row = _rows.FirstOrDefault(r => r.RefreshToken == presented);

        if (row == null || row.RevokedAt != null)
        {
            Note(StubEvent.Refused, $"invalid_grant: {(row == null ? "unknown" : "already rotated or revoked")} refresh token", presented);
            return Reply.Json(400, InvalidGrantBody);
        }

        if (row.App != clientId)
        {
            Note(StubEvent.Refused, $"invalid_grant: refresh token issued to {row.App}, presented by {clientId}", presented);
            return Reply.Json(400, InvalidGrantBody);
        }

        row.RevokedAt = DateTimeOffset.UtcNow;

        var next = _sequence + 1;
        var fresh = NewRow(row.App, row.User, $"at-{next}", $"rt-{next}");
        Note(StubEvent.Rotated, $"{presented} -> {fresh.RefreshToken}; access token {row.AccessToken} revoked with it, {fresh.AccessToken} issued", presented);

        return Reply.Json(200, TokenJson(fresh)) with { Delay = RotateReplyDelay };
    }

    private Reply Authorize(string clientId, string code)
    {
        var user = code.StartsWith("authz:", StringComparison.Ordinal) ? code["authz:".Length..] : "unknown";
        var next = _sequence + 1;
        var fresh = NewRow(clientId, user, $"at-{next}", $"rt-{next}");

        Note(StubEvent.Authorized, $"user {user} was issued {fresh.AccessToken}/{fresh.RefreshToken}; tokens issued to them earlier are untouched", null);

        return Reply.Json(200, TokenJson(fresh));
    }

    private Reply Revoke(System.Collections.Specialized.NameValueCollection form)
    {
        var token = form["token"] ?? string.Empty;

        lock (_gate)
        {
            var row = _rows.FirstOrDefault(r => r.RefreshToken == token || r.AccessToken == token);

            if (row != null) row.RevokedAt ??= DateTimeOffset.UtcNow;

            Note(StubEvent.Revoked, $"token {token} -> row {(row == null ? "none" : "#" + row.Id)}", token);
        }

        return Reply.Json(200, "{}");
    }

    // ─── REST ──────────────────────────────────────────────────────────────────

    private Reply Api(string? authorization, string path)
    {
        lock (_gate)
        {
            var row = FindLiveByAccessToken(BearerOf(authorization));

            Note(row == null ? StubEvent.Api401 : StubEvent.Api200, $"{path} Authorization={Describe(authorization)}", BearerOf(authorization));

            if (row == null) return Reply.Json(401, UnauthorizedBody) with { Headers = new Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer realm=\"Protected by OAuth 2.0\", error=\"invalid_token\"" } };

            return Reply.Json(200, ApiBody(row, path));
        }
    }

    private string ApiBody(TokenRow row, string path)
    {
        if (path == "/api/v4/user") return JsonSerializer.Serialize(new { id = 1, username = row.User });

        var member = path.IndexOf("/members/all/", StringComparison.Ordinal);

        if (path.StartsWith("/api/v4/projects/", StringComparison.Ordinal) && member >= 0)
            return JsonSerializer.Serialize(new { id = long.TryParse(path[(member + "/members/all/".Length)..], out var id) ? id : 0, access_level = MemberAccessLevel });

        return "[]";
    }

    // ─── git smart HTTP ────────────────────────────────────────────────────────

    private Reply GitAdvertisement(string? authorization, string? service)
    {
        var token = GitAccessTokenOf(authorization);

        lock (_gate)
        {
            var live = token != null && FindLiveByAccessToken(token) != null;

            Note(live ? StubEvent.Git200 : StubEvent.Git401, $"info/refs service={service} Authorization={Describe(authorization)}", token);

            if (!live) return new Reply(401, Encoding.UTF8.GetBytes("HTTP Basic: Access denied\n"), "text/plain", TimeSpan.Zero, new Dictionary<string, string> { ["WWW-Authenticate"] = "Basic realm=\"GitLab\"" });
        }

        return service is "git-upload-pack" or "git-receive-pack"
            ? new Reply(200, EmptyRepositoryAdvertisement(service), $"application/x-{service}-advertisement", TimeSpan.Zero, new Dictionary<string, string> { ["Cache-Control"] = "no-cache" })
            : Reply.Json(403, """{"message":"git smart-HTTP needs service=git-upload-pack or git-receive-pack"}""");
    }

    /// <summary>
    /// The access token a git request authenticates with, or null when it presents none GitLab would take: HTTP Basic
    /// takes an OAuth token only under the login <c>oauth2</c>; <c>Authorization: Bearer</c> carries it as it is.
    /// </summary>
    private static string? GitAccessTokenOf(string? authorization)
    {
        if (BearerOf(authorization) is { Length: > 0 } bearer) return bearer;
        if (authorization == null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..].Trim()));
            var separator = credentials.IndexOf(':');

            return separator > 0 && credentials[..separator] == "oauth2" ? credentials[(separator + 1)..] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The advertisement of a repository with no refs, which is what <c>git ls-remote</c> needs to see success: the service banner, a flush, the capabilities line on the null ref, a flush.</summary>
    private static byte[] EmptyRepositoryAdvertisement(string service)
    {
        var banner = PacketLine($"# service={service}\n");
        var capabilities = PacketLine($"{new string('0', 40)} capabilities^{{}}\0agent=codespace-stub\n");

        return Encoding.UTF8.GetBytes($"{banner}0000{capabilities}0000");
    }

    private static string PacketLine(string payload) => $"{Encoding.UTF8.GetByteCount(payload) + 4:x4}{payload}";

    // ─── State ─────────────────────────────────────────────────────────────────

    private TokenRow NewRow(string app, string user, string accessToken, string refreshToken)
    {
        var row = new TokenRow { Id = ++_sequence, App = app, User = user, AccessToken = accessToken, RefreshToken = refreshToken, CreatedAt = DateTimeOffset.UtcNow, ExpiresIn = ExpiresInSeconds };

        _rows.Add(row);

        return row;
    }

    private TokenRow? FindLiveByAccessToken(string? accessToken)
    {
        if (string.IsNullOrEmpty(accessToken)) return null;

        var row = _rows.FirstOrDefault(r => r.AccessToken == accessToken);

        return row != null && row.RevokedAt == null && row.CreatedAt.AddSeconds(row.ExpiresIn) > DateTimeOffset.UtcNow ? row : null;
    }

    private void Note(StubEvent kind, string detail, string? token)
    {
        lock (_gate) { _log.Add(new StubLogEntry(DateTimeOffset.UtcNow, kind, detail, token)); }
    }

    private static string? BearerOf(string? authorization) =>
        authorization != null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization["Bearer ".Length..].Trim() : null;

    private static string Describe(string? authorization) => authorization == null ? "-" : authorization.Split(' ')[0];

    private static string TokenJson(TokenRow row) =>
        JsonSerializer.Serialize(new { access_token = row.AccessToken, token_type = "Bearer", expires_in = row.ExpiresIn, refresh_token = row.RefreshToken, scope = "api", created_at = row.CreatedAt.ToUnixTimeSeconds() });

    /// <summary>
    /// The OS picks the port, but the probe has to let go of it before the listener can bind it, and in that gap a parallel
    /// test can take it. Either is a fact about the machine, not about the test, so binding again on a fresh port is the
    /// whole remedy.
    /// </summary>
    private static (HttpListener Listener, string BaseUrl) ListenOnFreeLoopbackPort()
    {
        for (var attempt = 1; ; attempt++)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var baseUrl = $"http://127.0.0.1:{port}";
            var listener = new HttpListener();
            listener.Prefixes.Add($"{baseUrl}/");

            try
            {
                listener.Start();
                return (listener, baseUrl);
            }
            catch (Exception) when (attempt < MaxBindAttempts)
            {
                listener.Close();
            }
        }
    }

    /// <summary>One token row, as Doorkeeper keeps it: the access token and its refresh token together.</summary>
    public sealed class TokenRow
    {
        public int Id { get; init; }
        public string App { get; init; } = string.Empty;
        public string User { get; init; } = string.Empty;
        public string AccessToken { get; init; } = string.Empty;
        public string RefreshToken { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public int ExpiresIn { get; init; }
        public DateTimeOffset? RevokedAt { get; set; }

        internal TokenRow Copy() => new() { Id = Id, App = App, User = User, AccessToken = AccessToken, RefreshToken = RefreshToken, CreatedAt = CreatedAt, ExpiresIn = ExpiresIn, RevokedAt = RevokedAt };

        public override string ToString() => $"#{Id} app={App} user={User} at={AccessToken} rt={RefreshToken} revoked={(RevokedAt.HasValue ? "yes" : "no")}";
    }

    public sealed record StubLogEntry(DateTimeOffset At, StubEvent Kind, string Detail, string? Token);

    private sealed record Reply(int Status, byte[] Body, string ContentType, TimeSpan Delay, IReadOnlyDictionary<string, string> Headers)
    {
        public static Reply Json(int status, string json) => new(status, Encoding.UTF8.GetBytes(json), "application/json", TimeSpan.Zero, new Dictionary<string, string>());
    }
}
