using CodeSpace.Core.Services.OutboundHttp;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Settings.OutboundHttp;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// HIGH fidelity (Rule 12): the http.request client as the REAL host registers it — <c>Program.CreateHostBuilder</c> +
/// <c>Startup</c>, the API's committed <c>appsettings.json</c>, real sockets against a loopback responder. Every other
/// tier builds its own registration, so none of them would notice the host losing its <c>AddGuardedHttpClient</c> line
/// (a merge-conflict casualty in a busy file): <c>CreateClient(nameof(HttpRequestNode))</c> would fall back to the
/// default client — redirects followed, no address check — and every other test would stay green. These pin that
/// registration, the committed empty allowlist, and that the allowlist is read while the host is being built.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class HttpRequestClientHostE2ETests : IClassFixture<WebhookApiFactory>
{
    private const string MetadataPath = "/latest/meta-data/iam/security-credentials/role";

    private readonly WebhookApiFactory _factory;

    public HttpRequestClientHostE2ETests(WebhookApiFactory factory) { _factory = factory; }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task The_hosts_http_request_client_refuses_the_workers_loopback(string host)
    {
        await using var metadata = LoopbackResponder.Start(_ => LoopbackResponder.Ok("{\"Token\":\"FAKE-LOOPBACK-SECRET\"}"));
        using var client = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HttpRequestNode));
        client.Timeout = TimeSpan.FromSeconds(10);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync($"http://{host}:{metadata.Port}{MetadataPath}"));

        ex.InnerException.ShouldBeOfType<OutboundDestinationRefusedException>(customMessage: $"the host's '{nameof(HttpRequestNode)}' client did not refuse http://{host} through the destination guard ({ex.Message}). Check Startup.ConfigureServices still calls AddGuardedHttpClient for it, and that CodeSpace.Api/appsettings.json commits an empty {OutboundHttpAllowlistSetting.ConfigurationKey}.");
        metadata.Accepted.ShouldBe(0, $"no socket may reach the worker's loopback through http://{host}");
    }

    [Fact]
    public void A_malformed_committed_entry_stops_the_host_from_starting()
    {
        // Built WITHOUT the fixture's database: the host must fail while its services are being registered, before
        // anything would touch one.
        using var unbooted = new WebhookApiFactory();
        using var factory = unbooted.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { [$"{OutboundHttpAllowlistSetting.ConfigurationKey}:0"] = "internal-api.corp:8080" })));

        var ex = Should.Throw<Exception>(() => factory.Services);

        Causes(ex).ShouldContain(cause => cause is InvalidOperationException && cause.Message.Contains("internal-api.corp:8080"), customMessage: $"the host must refuse to start on a malformed {OutboundHttpAllowlistSetting.ConfigurationKey} entry, naming it — otherwise it is found only when every http.request call fails at run time. Got: {ex}");
    }

    private static IEnumerable<Exception> Causes(Exception ex)
    {
        for (Exception? cause = ex; cause != null; cause = cause.InnerException) yield return cause;
    }
}
