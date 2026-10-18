using System.Net;
using System.Net.Sockets;
using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.UnitTests.OutboundHttp;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// The real probe against loopback hosts: it asks a GitLab's API root, with no credential, whether the address redirects to
/// another origin — and says nothing else, ever. Anything that is not a redirect to another origin (any answer, a host that
/// is down, one that never answers, a URL that is not http(s), a provider with no API root to ask) leaves the address as the
/// owner wrote it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ProviderEndpointProbeTests : IAsyncDisposable
{
    private readonly List<LoopbackResponder> _hosts = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
    }

    [Theory]
    [InlineData(200)]
    [InlineData(401)]       // GitLab wants a token for /version: the answer is not read, only whether it redirects
    [InlineData(404)]
    [InlineData(500)]
    public async Task An_answer_that_is_not_a_redirect_is_no_verdict_whatever_its_status(int status)
    {
        var gitlab = Host(_ => $"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        await Probe().EnsureNoRedirectToAnotherOriginAsync(ProviderKind.GitLab, $"http://127.0.0.1:{gitlab.Port}", CancellationToken.None);

        gitlab.Requests.ShouldHaveSingleItem().ShouldStartWith("GET /api/v4/version HTTP/1.1");
    }

    [Theory]
    [InlineData("http to https", "https://127.0.0.1:{0}/api/v4/version")]
    [InlineData("host alias", "http://localhost:{0}/api/v4/version")]
    [InlineData("other port", "http://127.0.0.1:{0}/api/v4/version")]
    public async Task A_redirect_to_another_origin_is_named_and_not_followed(string shape, string locationTemplate)
    {
        var elsewhere = Host(_ => LoopbackResponder.Ok("{}"));
        var location = string.Format(locationTemplate, elsewhere.Port);
        var gitlab = Host(_ => LoopbackResponder.Redirect(location, 301));

        var failure = await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => Probe().EnsureNoRedirectToAnotherOriginAsync(ProviderKind.GitLab, $"http://127.0.0.1:{gitlab.Port}", CancellationToken.None));

        failure.FinalUrl.ShouldBe(location, shape);
        failure.RequestedOrigin.ShouldBe($"http://127.0.0.1:{gitlab.Port}");
        elsewhere.Accepted.ShouldBe(0, "the probe never goes where it was pointed");
    }

    [Fact]
    public async Task A_redirect_inside_the_origin_is_followed_and_what_it_leads_to_is_the_verdict()
    {
        var elsewhere = Host(_ => LoopbackResponder.Ok("{}"));
        var gitlab = Host(head => head.StartsWith("GET /api/v4/version ") ? LoopbackResponder.Redirect("/api/v4/version/", 301) : LoopbackResponder.Redirect($"http://127.0.0.1:{elsewhere.Port}/x", 302));

        var failure = await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => Probe().EnsureNoRedirectToAnotherOriginAsync(ProviderKind.GitLab, $"http://127.0.0.1:{gitlab.Port}", CancellationToken.None));

        failure.FinalUrl.ShouldBe($"http://127.0.0.1:{elsewhere.Port}/x");
        gitlab.Requests.Count.ShouldBe(2, "the path that normalises on the way was followed once");
    }

    [Fact]
    public async Task A_redirect_inside_the_origin_that_ends_in_an_answer_is_no_verdict()
    {
        var gitlab = Host(head => head.StartsWith("GET /api/v4/version ") ? LoopbackResponder.Redirect("/api/v4/version/", 301) : LoopbackResponder.Ok("{}"));

        await Probe().EnsureNoRedirectToAnotherOriginAsync(ProviderKind.GitLab, $"http://127.0.0.1:{gitlab.Port}", CancellationToken.None);
    }

    [Fact]
    public async Task The_path_of_a_relative_url_root_is_kept_and_the_query_userinfo_and_secrets_are_not_sent()
    {
        var gitlab = Host(_ => LoopbackResponder.Ok("{}"));

        await Probe().EnsureNoRedirectToAnotherOriginAsync(ProviderKind.GitLab, $"http://svc:hunter2@127.0.0.1:{gitlab.Port}/gitlab/?private_token=SECRET#frag", CancellationToken.None);

        var head = gitlab.Requests.ShouldHaveSingleItem();
        head.ShouldStartWith("GET /gitlab/api/v4/version HTTP/1.1");
        head.ToLowerInvariant().ShouldNotContain("authorization");
        head.ToLowerInvariant().ShouldNotContain("private-token");
        head.ToLowerInvariant().ShouldNotContain("cookie");
        head.ShouldNotContain("hunter2");
        head.ShouldNotContain("SECRET");
    }

    [Fact]
    public async Task A_host_that_is_down_is_no_verdict_and_answers_fast()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        await Probe().EnsureNoRedirectToAnotherOriginAsync(ProviderKind.GitLab, $"http://127.0.0.1:{port}", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_host_that_never_answers_is_no_verdict_after_the_timeout()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        var probe = new ProviderEndpointProbe(NullLogger<ProviderEndpointProbe>.Instance, TimeSpan.FromMilliseconds(400));

        await probe.EnsureNoRedirectToAnotherOriginAsync(ProviderKind.GitLab, $"http://127.0.0.1:{port}", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_caller_that_cancels_is_not_swallowed()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => Probe().EnsureNoRedirectToAnotherOriginAsync(ProviderKind.GitLab, $"http://127.0.0.1:{port}", cancel.Token));
    }

    [Theory]
    [InlineData("gitlab.example.com")]              // no scheme
    [InlineData("ftp://gitlab.example.com")]
    [InlineData("")]
    public async Task An_address_that_is_not_an_http_url_is_not_asked_about(string url)
    {
        await Probe().EnsureNoRedirectToAnotherOriginAsync(ProviderKind.GitLab, url, CancellationToken.None);
    }

    [Theory]
    [InlineData(ProviderKind.GitHub)]
    public async Task A_provider_with_no_api_root_to_ask_is_not_asked(ProviderKind provider)
    {
        var host = Host(_ => LoopbackResponder.Redirect("https://elsewhere.example.test/", 301));

        await Probe().EnsureNoRedirectToAnotherOriginAsync(provider, $"http://127.0.0.1:{host.Port}", CancellationToken.None);

        host.Accepted.ShouldBe(0);
    }

    // ── Does the address answer HTTP at all — asked of an https upgrade while tokens are connected ──

    [Theory]
    [InlineData(200)]
    [InlineData(401)]       // an answer is an answer: the route may want a token, the host is there and speaks HTTP
    [InlineData(404)]
    [InlineData(500)]
    public async Task Any_http_answer_is_an_answer_whatever_its_status(int status)
    {
        var gitlab = Host(_ => $"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        await Probe().EnsureAnswersAsync(ProviderKind.GitLab, $"http://127.0.0.1:{gitlab.Port}/gitlab", CancellationToken.None);

        gitlab.Requests.ShouldHaveSingleItem().ShouldStartWith("GET /gitlab HTTP/1.1");
    }

    [Fact]
    public async Task A_redirect_to_another_origin_is_an_answer_and_is_not_followed()
    {
        var elsewhere = Host(_ => LoopbackResponder.Ok("{}"));
        var gitlab = Host(_ => LoopbackResponder.Redirect($"http://127.0.0.1:{elsewhere.Port}/x", 301));

        await Probe().EnsureAnswersAsync(ProviderKind.GitLab, $"http://127.0.0.1:{gitlab.Port}", CancellationToken.None);

        elsewhere.Accepted.ShouldBe(0, "where a redirect leads is the redirect probe's to say, not this one's");
    }

    [Fact]
    public async Task An_https_address_that_speaks_plain_http_is_not_an_answer()
    {
        // The address a mistaken upgrade names: a host whose port does not do TLS. The handshake never completes.
        var plain = Host(_ => LoopbackResponder.Ok("{}"));
        var probe = new ProviderEndpointProbe(NullLogger<ProviderEndpointProbe>.Instance, TimeSpan.FromMilliseconds(500));

        var failure = await Should.ThrowAsync<ProviderEndpointUnreachableException>(() => probe.EnsureAnswersAsync(ProviderKind.GitLab, $"https://127.0.0.1:{plain.Port}", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));

        failure.Origin.ShouldBe($"https://127.0.0.1:{plain.Port}");
        failure.ProviderKind.ShouldBe(ProviderKind.GitLab);
    }

    [Fact]
    public async Task A_host_that_is_down_is_not_an_answer()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        await Should.ThrowAsync<ProviderEndpointUnreachableException>(() => Probe().EnsureAnswersAsync(ProviderKind.GitLab, $"https://127.0.0.1:{port}", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task A_host_that_never_answers_is_not_an_answer_after_the_timeout()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        var probe = new ProviderEndpointProbe(NullLogger<ProviderEndpointProbe>.Instance, TimeSpan.FromMilliseconds(400));

        await Should.ThrowAsync<ProviderEndpointUnreachableException>(() => probe.EnsureAnswersAsync(ProviderKind.GitLab, $"http://127.0.0.1:{port}", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task A_caller_that_cancels_an_answer_check_is_not_swallowed()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => Probe().EnsureAnswersAsync(ProviderKind.GitLab, $"http://127.0.0.1:{port}", cancel.Token));
    }

    [Fact]
    public async Task The_answer_check_sends_no_credential_userinfo_query_or_cookie()
    {
        var gitlab = Host(_ => LoopbackResponder.Ok("{}"));

        await Probe().EnsureAnswersAsync(ProviderKind.GitHub, $"http://svc:hunter2@127.0.0.1:{gitlab.Port}/gitlab/?private_token=SECRET#frag", CancellationToken.None);

        var head = gitlab.Requests.ShouldHaveSingleItem();
        head.ShouldStartWith("GET /gitlab HTTP/1.1");
        head.ToLowerInvariant().ShouldNotContain("authorization");
        head.ToLowerInvariant().ShouldNotContain("private-token");
        head.ToLowerInvariant().ShouldNotContain("cookie");
        head.ShouldNotContain("hunter2");
        head.ShouldNotContain("SECRET");
    }

    [Theory]
    [InlineData("gitlab.example.com")]              // no scheme
    [InlineData("ftp://gitlab.example.com")]
    [InlineData("")]
    public async Task An_address_that_is_not_an_http_url_has_nothing_to_answer(string url)
    {
        await Probe().EnsureAnswersAsync(ProviderKind.GitLab, url, CancellationToken.None);
    }

    private static ProviderEndpointProbe Probe() => new(NullLogger<ProviderEndpointProbe>.Instance);

    private LoopbackResponder Host(Func<string, string> respond)
    {
        var host = LoopbackResponder.Start(respond);
        _hosts.Add(host);

        return host;
    }
}
