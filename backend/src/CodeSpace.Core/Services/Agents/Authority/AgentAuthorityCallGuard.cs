using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Authority;

/// <summary>Authorization failure has a stable model-facing meaning and a durable, argument-free audit record.</summary>
public interface IAgentAuthorityCallGuard
{
    Task<AuthorityCallFailure?> CheckAsync(Guid runId, Guid teamId, string toolKind, CancellationToken cancellationToken);
}

public sealed class AgentAuthorityCallGuard : IAgentAuthorityCallGuard, IScopedDependency
{
    private readonly ExecutionAuthorityService _authority;
    private readonly IAgentRunService _runs;
    private readonly ILogger<AgentAuthorityCallGuard> _logger;

    public AgentAuthorityCallGuard(ExecutionAuthorityService authority, IAgentRunService runs, ILogger<AgentAuthorityCallGuard> logger)
    {
        _authority = authority;
        _runs = runs;
        _logger = logger;
    }

    public async Task<AuthorityCallFailure?> CheckAsync(Guid runId, Guid teamId, string toolKind, CancellationToken cancellationToken)
    {
        AuthorityCallFailure failure;
        try
        {
            await _authority.EnsureAgentActionAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (AgentAuthorityDeniedException ex)
        {
            failure = new AuthorityCallFailure(ex.Code, ex.Reason, ex.Message, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Cannot verify agent execution authority. AgentRunId={AgentRunId} TeamId={TeamId}", runId, teamId);
            failure = new AuthorityCallFailure("agent.authority_unavailable", "verification-unavailable", "Execution authority could not be verified. The tool was not executed; retry after the authority service recovers.", true);
        }

        _logger.LogWarning("Agent tool authority refused. AgentRunId={AgentRunId} TeamId={TeamId} Tool={Tool} Code={Code} Reason={Reason}", runId, teamId, toolKind, failure.Code, failure.Reason);
        try
        {
            await _runs.AppendSystemEventAsync(runId, new AgentEvent { Kind = AgentEventKind.Warning, Text = failure.Message, Data = JsonSerializer.SerializeToElement(new { kind = "authority.denied", toolKind, failure.Code, failure.Reason, failure.Retryable }, AgentJson.Options) }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Losing audit storage cannot authorize the action. The structured application log remains explicit.
            _logger.LogError(ex, "Agent authority denial audit could not be persisted. AgentRunId={AgentRunId} Code={Code}", runId, failure.Code);
        }
        return failure;
    }
}

public sealed record AuthorityCallFailure(string Code, string Reason, string Message, bool Retryable);
