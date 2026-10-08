using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CodeSpace.Core.Services.OutboundHttp;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Core.Settings.OutboundHttp;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.OutboundHttp;

/// <summary>
/// The PRODUCTION registration (<see cref="GuardedHttpClientRegistration"/>) driven over real sockets against loopback
/// responders — the shapes the SSRF audit proved reachable through http.request, now refused. Each refusal also asserts
/// the responder ACCEPTED NO CONNECTION: a test that only checked for a failed request would pass just as well if the
/// guard let the connection through and the request then failed for some other reason.
///
/// <para>Each guard is built here with an explicit proxy — none, or a loopback responder playing the operator's proxy —
/// so a developer's own HTTP(S)_PROXY cannot change what these assert. Names other than <c>localhost</c> go to a stubbed
/// resolver (<see cref="TestDns"/>), so no lookup leaves the process.</para>
/// </summary>
[Trait("Category", "Unit")]
public class GuardedHttpClientTests
{
    private const string MetadataPath = "/latest/meta-data/iam/security-credentials/role";
    private const string FakeCredential = "{\"AccessKeyId\":\"FAKE-AKID\",\"Token\":\"FAKE-LOOPBACK-SECRET\"}";

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("2130706433")]                   // decimal
    [InlineData("0x7f000001")]                   // hex
    [InlineData("127.1")]                        // short form
    [InlineData("0177.0.0.1")]                   // octal
    [InlineData("0.0.0.0")]                      // "this host" — connects to loopback
    [InlineData("[::1]")]
    [InlineData("[::]")]
    [InlineData("[::ffff:127.0.0.1]")]           // v4-mapped
    [InlineData("[0:0:0:0:0:ffff:7f00:1]")]      // v4-mapped, long hex form
    public async Task Every_spelling_of_loopback_is_refused_before_a_connection_is_made(string host)
    {
        await using var metadata = LoopbackResponder.Start(_ => LoopbackResponder.Ok(FakeCredential));
        using var factory = GuardedFactory();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => factory.Client.GetAsync($"http://{host}:{metadata.Port}{MetadataPath}"));

        ex.InnerException.ShouldBeOfType<OutboundDestinationRefusedException>($"http://{host} must be refused by the destination guard, not fail some other way: {ex.Message}");
        metadata.Accepted.ShouldBe(0, $"no socket may reach the loopback responder through http://{host}");
    }

    [Theory]
    [InlineData("127.0.0.0/8", "127.0.0.1")]     // CIDR entry
    [InlineData("localhost", "localhost")]       // host entry: admits ::1 and 127.0.0.1 — the dial tries both
    public async Task An_allowlisted_internal_destination_answers_in_full(string entry, string host)
    {
        await using var service = LoopbackResponder.Start(_ => LoopbackResponder.Ok(FakeCredential));
        using var factory = GuardedFactory(entry);

        using var response = await factory.Client.GetAsync($"http://{host}:{service.Port}{MetadataPath}");

        response.IsSuccessStatusCode.ShouldBeTrue();
        (await response.Content.ReadAsStringAsync()).ShouldBe(FakeCredential);
    }

    [Fact]
    public async Task A_redirect_from_an_admitted_host_to_loopback_is_refused_at_the_redirect_hop()
    {
        // The audit's P3: the author pins a host, the host answers 302 to the worker's loopback. The first hop is
        // admitted (the operator allowlisted that host by name); the second must be refused when it connects.
        await using var metadata = LoopbackResponder.Start(_ => LoopbackResponder.Ok(FakeCredential));
        await using var front = LoopbackResponder.Start(_ => LoopbackResponder.Redirect($"http://127.0.0.1:{metadata.Port}{MetadataPath}"));
        using var factory = GuardedFactory("localhost");

        var ex = await Should.ThrowAsync<HttpRequestException>(() => factory.Client.GetAsync($"http://localhost:{front.Port}/feature-x"));

        ex.InnerException.ShouldBeOfType<OutboundDestinationRefusedException>(ex.Message);
        front.Requests.Count.ShouldBe(1, "the admitted first hop was made");
        metadata.Accepted.ShouldBe(0, "the redirect target is loopback and not allowlisted — no socket may reach it");
    }

    [Fact]
    public async Task A_cross_origin_redirect_delivers_none_of_the_author_headers()
    {
        await using var target = LoopbackResponder.Start(_ => LoopbackResponder.Ok("{\"ok\":true}"));
        await using var front = LoopbackResponder.Start(_ => LoopbackResponder.Redirect($"http://127.0.0.1:{target.Port}/data"));
        using var factory = GuardedFactory("localhost", "127.0.0.1");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{front.Port}/start");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "FAKE-TEAM-API-KEY");
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", "FAKE-PRIVATE-TOKEN");
        request.Headers.TryAddWithoutValidation("Cookie", "sid=FAKE-COOKIE");

        using var response = await factory.Client.SendAsync(request);

        response.IsSuccessStatusCode.ShouldBeTrue();
        front.Requests.Single().ShouldContain("X-Api-Key: FAKE-TEAM-API-KEY", customMessage: "the origin the author wrote the headers for receives them");

        var redirected = target.Requests.Single();
        redirected.ShouldNotContain("FAKE-TEAM-API-KEY");
        redirected.ShouldNotContain("FAKE-PRIVATE-TOKEN");
        redirected.ShouldNotContain("FAKE-COOKIE");
    }

    [Fact]
    public async Task The_http_request_node_refuses_a_loopback_url_built_from_trigger_text()
    {
        // The audit's P1, end to end through the real node: the URL is the untrusted {{trigger.body}}, resolved by the
        // engine's own VariableResolver.
        await using var metadata = LoopbackResponder.Start(_ => LoopbackResponder.Ok(FakeCredential));
        using var factory = GuardedFactory();

        var result = await new HttpRequestNode(factory.Factory).RunAsync(Context(new { url = "{{trigger.body}}", method = "GET" }, $"http://127.0.0.1:{metadata.Port}{MetadataPath}"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldNotBeNull().ShouldContain("Refused to connect to '127.0.0.1'");
        result.Retryable.ShouldBeFalse("a refused destination is deterministic; retrying it only burns attempts");
        result.Outputs.ShouldNotContainKey("body", "nothing from the loopback responder may become a node output");
        metadata.Accepted.ShouldBe(0);
    }

    [Fact]
    public async Task A_cookie_one_run_was_given_is_never_sent_by_another()
    {
        // Every CreateClient(nameof(HttpRequestNode)) in the process shares one primary handler for its lifetime, across
        // runs and teams. With the handler's own cookie jar, run B's call carried the session run A had just been given.
        await using var service = LoopbackResponder.Start(head => head.StartsWith("GET /login", StringComparison.Ordinal) ? LoopbackResponder.Ok("{}", "Set-Cookie: sid=FAKE-TEAM-A-SESSION; Path=/") : LoopbackResponder.Ok("{}"));
        using var factory = GuardedFactory("localhost");

        var teamA = await new HttpRequestNode(factory.Factory).RunAsync(Context(new { url = $"http://localhost:{service.Port}/login", method = "GET" }), CancellationToken.None);
        var teamB = await new HttpRequestNode(factory.Factory).RunAsync(Context(new { url = $"http://localhost:{service.Port}/me", method = "GET" }), CancellationToken.None);
        var authored = await new HttpRequestNode(factory.Factory).RunAsync(Context(new { url = $"http://localhost:{service.Port}/mine", method = "GET", headers = new Dictionary<string, string> { ["Cookie"] = "sid=FAKE-AUTHORED" } }), CancellationToken.None);

        new[] { teamA.Status, teamB.Status, authored.Status }.ShouldAllBe(status => status == NodeStatus.Success);
        RequestTo(service, "/me").ShouldNotContain("Cookie:", customMessage: "a cookie set for one run must never ride along on another run's request");
        RequestTo(service, "/mine").ShouldContain("Cookie: sid=FAKE-AUTHORED", customMessage: "a Cookie header the author wrote still reaches the origin it was written for");
        RequestTo(service, "/mine").ShouldNotContain("FAKE-TEAM-A-SESSION");
    }

    [Fact]
    public async Task An_author_host_header_cannot_reach_another_virtual_host_behind_an_admitted_name()
    {
        // An operator admits ONE name. Host (and the TLS SNI the handler derives from it) is what picks the virtual host
        // behind that address, so an author-written Host would reach every other vhost on a shared ingress.
        await using var ingress = LoopbackResponder.Start(_ => LoopbackResponder.Ok("{}"));
        using var factory = GuardedFactory("localhost");

        var result = await new HttpRequestNode(factory.Factory).RunAsync(Context(new { url = $"http://localhost:{ingress.Port}/admin", method = "GET", headers = new Dictionary<string, string> { ["Host"] = "secret-admin.internal" } }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, result.Error);
        RequestTo(ingress, "/admin").ShouldContain($"Host: localhost:{ingress.Port}", customMessage: "the request is addressed to the authority the guard checked");
        RequestTo(ingress, "/admin").ShouldNotContain("secret-admin.internal");
    }

    [Fact]
    public async Task A_cross_origin_307_returns_the_redirect_instead_of_sending_the_body_on()
    {
        // The audit's body twin of the header leak: a token endpoint answers 307 to another origin, and a 307 re-sends
        // the body — the client_secret the author wrote for the first origin, often from a team secret.
        await using var collector = LoopbackResponder.Start(_ => LoopbackResponder.Ok("{}"));
        await using var front = LoopbackResponder.Start(_ => LoopbackResponder.Redirect($"http://127.0.0.1:{collector.Port}/collect", 307));
        using var factory = GuardedFactory("localhost", "127.0.0.1");

        var result = await new HttpRequestNode(factory.Factory).RunAsync(Context(new { url = $"http://localhost:{front.Port}/token", method = "POST", body = new { client_secret = "FAKE-BODY-SECRET" } }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, result.Error);
        result.Outputs["status"].GetInt32().ShouldBe(307, "the redirect comes back to the workflow, which can follow it deliberately");
        collector.Accepted.ShouldBe(0, "no socket may carry the author's body to the redirect's origin");
    }

    [Theory]
    [InlineData("::1", "v4-first.example.test", "127.0.0.1", "::1")]
    [InlineData("127.0.0.0/8", "v6-first.example.test", "::1", "127.0.0.1")]
    public async Task A_mixed_answer_is_dialled_only_at_the_addresses_the_guard_permits(string allowed, string host, string refused, string permitted)
    {
        // The DNS-rebinding shape at socket level: one name answering with an address the guard refuses AND one it
        // admits, the refused one FIRST. Both listen on the same port, so only the dial decides where the socket goes —
        // a guard that checked the answer but dialled all of it would land on the refused address.
        await using var service = LoopbackResponder.Start(_ => LoopbackResponder.Ok("{}"));

        if (!service.ListensOnIpv6) return;   // a host without IPv6 loopback cannot stage a two-family answer

        using var factory = GuardedFactory(new OutboundDestinationGuard(OutboundDestinationAllowlist.Parse(new[] { allowed }), Direct, TestDns));

        using var response = await factory.Client.GetAsync($"http://{host}:{service.Port}/");

        response.IsSuccessStatusCode.ShouldBeTrue();
        service.AcceptedOn(IPAddress.Parse(refused)).ShouldBe(0, $"{host} answered {refused} first, and the guard refuses it — no socket may be dialled there");
        service.AcceptedOn(IPAddress.Parse(permitted)).ShouldBe(1);
    }

    [Fact]
    public async Task A_public_destination_is_reached_through_the_operators_proxy()
    {
        // A worker that reaches the internet only through HTTP(S)_PROXY. The proxy's own address is internal (loopback
        // here) and is dialled as the operator's proxy; the public destination behind it answers. 203.0.113.10 is
        // TEST-NET-3: public to the guard and routed nowhere, so a regression that dialled it directly reaches no host.
        await using var proxy = LoopbackResponder.Start(_ => LoopbackResponder.Ok("{\"via\":\"operator-proxy\"}"));
        using var factory = GuardedFactory(ProxiedGuard(proxy));

        using var response = await factory.Client.GetAsync("http://api.example.test/v1/items");

        (await response.Content.ReadAsStringAsync()).ShouldBe("{\"via\":\"operator-proxy\"}");
        proxy.Requests.Single().ShouldStartWith("GET http://api.example.test/v1/items HTTP/1.1", customMessage: "the proxy is asked for the destination itself");
    }

    [Theory]
    [InlineData("http://metadata.example.test/latest/meta-data/")]
    [InlineData("https://internal.example.test/admin")]   // would be a CONNECT tunnel
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("http://[::ffff:10.0.0.5]/admin")]
    public async Task A_request_the_proxy_would_carry_to_an_internal_address_is_refused_before_it_is_sent(string url)
    {
        // Through a proxy the connect-time check sees only the proxy, so the destination is checked before the request
        // leaves: the proxy must never be asked to fetch it.
        await using var proxy = LoopbackResponder.Start(_ => LoopbackResponder.Ok("{}"));
        using var factory = GuardedFactory(ProxiedGuard(proxy));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => factory.Client.GetAsync(url));

        ex.InnerException.ShouldBeOfType<OutboundDestinationRefusedException>(ex.Message);
        proxy.Accepted.ShouldBe(0, $"the proxy must not be asked for {url}");
    }

    [Fact]
    public async Task A_redirect_through_the_proxy_to_an_internal_address_is_refused_at_that_hop()
    {
        await using var proxy = LoopbackResponder.Start(_ => LoopbackResponder.Redirect("http://metadata.example.test/latest/meta-data/"));
        using var factory = GuardedFactory(ProxiedGuard(proxy));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => factory.Client.GetAsync("http://api.example.test/feature-x"));

        ex.InnerException.ShouldBeOfType<OutboundDestinationRefusedException>(ex.Message);
        proxy.Requests.Count.ShouldBe(1, "only the public first hop went through the proxy");
    }

    [Fact]
    public void A_malformed_committed_entry_fails_the_registration_not_the_first_request()
    {
        // The host registers the client from configuration at boot, so a bad entry stops the host from starting instead
        // of failing every http.request — public ones included — at run time.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"{OutboundHttpAllowlistSetting.ConfigurationKey}:0"] = "internal-api.corp:8080" }).Build();

        var ex = Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddGuardedHttpClient(nameof(HttpRequestNode), configuration));

        ex.Message.ShouldContain("internal-api.corp:8080");
    }

    private static string RequestTo(LoopbackResponder responder, string path) => responder.Requests.Single(head => head.StartsWith($"GET {path} ", StringComparison.Ordinal));

    private static NodeRunContext Context(object inputs, string triggerBody = "")
    {
        var raw = JsonSerializer.SerializeToElement(inputs);
        var scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement> { ["body"] = JsonSerializer.SerializeToElement(triggerBody) } };
        var empty = JsonSerializer.SerializeToElement(new { });

        return new NodeRunContext
        {
            Inputs = VariableResolver.ResolveBag(raw, scope),
            Config = new Dictionary<string, JsonElement> { ["timeoutSeconds"] = JsonSerializer.SerializeToElement(5) },
            RawConfig = empty,
            RawInputs = raw,
            Scope = scope,
            Logger = NullLogger.Instance,
            Observability = NodeObservability.NoOp,
        };
    }

    /// <summary>No proxy address, so <see cref="WebProxy"/> bypasses every destination and each request connects directly through the guard.</summary>
    private static readonly IWebProxy Direct = new WebProxy();

    /// <summary>The names these tests resolve. 203.0.113.10 is TEST-NET-3: public to the guard, routed nowhere.</summary>
    private static readonly Dictionary<string, string[]> StubbedNames = new()
    {
        ["api.example.test"] = new[] { "203.0.113.10" },
        ["metadata.example.test"] = new[] { "169.254.169.254" },
        ["internal.example.test"] = new[] { "10.0.0.5" },
        ["v4-first.example.test"] = new[] { "127.0.0.1", "::1" },
        ["v6-first.example.test"] = new[] { "::1", "127.0.0.1" },
    };

    private static Task<IPAddress[]> TestDns(string host, CancellationToken _) => StubbedNames.TryGetValue(host, out var answer) ? Task.FromResult(answer.Select(IPAddress.Parse).ToArray()) : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));

    /// <summary>A guard whose operator proxy is <paramref name="proxy"/>, as HTTP(S)_PROXY would name it, with nothing allowlisted.</summary>
    private static OutboundDestinationGuard ProxiedGuard(LoopbackResponder proxy) => new(OutboundDestinationAllowlist.Empty, new WebProxy($"http://127.0.0.1:{proxy.Port}"), TestDns);

    /// <summary>The production registration over a guard admitting <paramref name="allowed"/>, connecting directly and resolving through the system resolver.</summary>
    private static GuardedClientFactory GuardedFactory(params string[] allowed) => GuardedFactory(new OutboundDestinationGuard(OutboundDestinationAllowlist.Parse(allowed), Direct));

    private static GuardedClientFactory GuardedFactory(OutboundDestinationGuard guard)
    {
        var services = new ServiceCollection();
        services.AddGuardedHttpClient(nameof(HttpRequestNode), guard);

        return new GuardedClientFactory(services.BuildServiceProvider());
    }

    private sealed class GuardedClientFactory : IDisposable
    {
        private readonly ServiceProvider _provider;

        public GuardedClientFactory(ServiceProvider provider)
        {
            _provider = provider;
            Factory = provider.GetRequiredService<IHttpClientFactory>();
            Client = Factory.CreateClient(nameof(HttpRequestNode));
            Client.Timeout = TimeSpan.FromSeconds(10);
        }

        public IHttpClientFactory Factory { get; }

        public HttpClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            _provider.Dispose();
        }
    }
}
