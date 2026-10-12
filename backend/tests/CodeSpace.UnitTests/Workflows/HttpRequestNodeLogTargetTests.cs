using System.Net;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The operator log line <c>http.request</c> writes before the call. It names where the call goes and nothing more: a
/// URL's userinfo (<c>https://user:{{team.PASSWORD}}@host</c>) is a credential, and this line reaches the shared log
/// sink at Information, before the ledger's redaction or the outbound guard ever run.
/// </summary>
[Trait("Category", "Unit")]
public sealed class HttpRequestNodeLogTargetTests
{
    [Theory]
    [InlineData("https://svc-user:CANARY-userinfo-61a0@api.example.test/v1/deploy?token=CANARY-query-61a0", "https://api.example.test")]
    [InlineData("http://svc-user:CANARY-userinfo-61a0@api.example.test:8443/v1", "http://api.example.test:8443")]
    public async Task The_log_target_is_scheme_host_and_port_only(string url, string expected)
    {
        var logger = new CapturingLogger();

        var result = await new HttpRequestNode(new StubFactory()).RunAsync(Context(url, logger), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, result.Error);
        var line = logger.Lines.First(entry => entry.StartsWith("HTTP GET ", StringComparison.Ordinal));
        line.ShouldBe($"HTTP GET {expected} (timeout=30s)");
        string.Join('\n', logger.Lines).ShouldNotContain("CANARY", Case.Sensitive, "no part of a credential reaches the operator log");
    }

    private static NodeRunContext Context(string url, ILogger logger) => new()
    {
        Inputs = new Dictionary<string, JsonElement> { ["url"] = JsonSerializer.SerializeToElement(url), ["method"] = JsonSerializer.SerializeToElement("GET") },
        Config = new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = logger,
        Observability = NodeObservability.NoOp,
    };

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new OkHandler());
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
