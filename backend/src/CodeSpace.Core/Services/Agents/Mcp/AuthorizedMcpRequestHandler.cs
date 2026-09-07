using System.Text.Json;
using CodeSpace.Core.Services.Agents.Authority;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>Every production endpoint checks fresh authority at request entry and again immediately before invocation.</summary>
public sealed class AuthorizedMcpRequestHandler : IMcpRequestHandler
{
    private readonly IMcpRequestHandler _inner;
    private readonly McpAuthorityContext _context;

    public AuthorizedMcpRequestHandler(IMcpRequestHandler inner, McpAuthorityContext context)
    {
        _inner = inner;
        _context = context;
    }

    public async Task<JsonElement?> HandleAsync(JsonElement request, CancellationToken cancellationToken)
    {
        if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out var id) && request.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() == "tools/call")
        {
            var kind = request.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "unknown";
            if (await _context.Guard.CheckAsync(_context.AgentRunId, _context.TeamId, kind, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                _context.Counters?.MarkToolCall();
                return JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id, result = new { isError = true, content = new[] { new { type = "text", text = $"{failure.Code}: {failure.Message}" } }, structuredContent = failure } }, AgentJson.Options);
            }
        }
        return await _inner.HandleAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record McpAuthorityContext(Guid AgentRunId, Guid TeamId, IAgentAuthorityCallGuard Guard, McpFabricCounters? Counters = null);

/// <summary>Wraps the complete registry, so direct/read-only/ledger/approval invocation paths all recheck after waiting.</summary>
public sealed class AuthorityCheckedToolRegistry : IAgentToolRegistry
{
    private readonly IAgentToolRegistry _inner;
    private readonly McpAuthorityContext _context;

    public AuthorityCheckedToolRegistry(IAgentToolRegistry inner, McpAuthorityContext context)
    {
        _inner = inner;
        _context = context;
    }

    public IReadOnlyList<IAgentTool> All => _inner.All.Select(tool => (IAgentTool)new CheckedTool(tool, _context)).ToArray();
    public IAgentTool? Resolve(string kind) => _inner.Resolve(kind) is { } tool ? new CheckedTool(tool, _context) : null;

    private sealed class CheckedTool : IAgentTool
    {
        private readonly IAgentTool _inner;
        private readonly McpAuthorityContext _context;
        public CheckedTool(IAgentTool inner, McpAuthorityContext context) { _inner = inner; _context = context; }
        public string Kind => _inner.Kind;
        public string Description => _inner.Description;
        public IReadOnlyList<string> Aliases => _inner.Aliases;
        public string? SearchHint => _inner.SearchHint;
        public JsonElement InputSchema => _inner.InputSchema;
        public JsonElement OutputSchema => _inner.OutputSchema;
        public bool IsReadOnly => _inner.IsReadOnly;
        public bool IsConcurrencySafe => _inner.IsConcurrencySafe;
        public bool IsDestructive => _inner.IsDestructive;
        public bool RequiresApproval => _inner.RequiresApproval;
        public bool AlwaysRequiresApproval => _inner.AlwaysRequiresApproval;
        public AgentToolValidation ValidateInput(JsonElement input) => _inner.ValidateInput(input);
        public async Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken)
        {
            if (await _context.Guard.CheckAsync(_context.AgentRunId, _context.TeamId, Kind, cancellationToken).ConfigureAwait(false) is { } failure) return AgentToolResult.Fail($"{failure.Code}: {failure.Message}");
            return await _inner.CallAsync(call with { RunId = _context.AgentRunId, TeamId = _context.TeamId }, cancellationToken).ConfigureAwait(false);
        }
    }
}
