using System.Text.Json;
using CodeSpace.Core.Persistence;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Workflows;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The one rule for a workflow secret that rides an agent's task: the stager names it
/// (<see cref="AgentTask.WorkflowSecrets"/>), the store persists the task masked with the exact copy sealed beside it,
/// and the executor opens that copy — so the agent works with the real value while <c>task_jsonb</c>, the goal a
/// Viewer reads and every log the run writes hold only the mask.
/// </summary>
public static class AgentTaskSecrets
{
    /// <summary>
    /// <paramref name="task"/> naming every value of <paramref name="scope"/> its text embeds, added to any it already
    /// names. Only values long enough to be an agent-plane needle (<see cref="AgentRunExecutor.MinimumNeedleLength"/>)
    /// count: the same values are folded into the run's stream redactor, which leaves shorter fragments readable rather
    /// than shredding every line that happens to contain them, and keeping the two sets equal keeps every structural
    /// field of the persisted task (an enum, a flag, a count) intact. A null scope or a task embedding nothing returns
    /// the task unchanged.
    /// </summary>
    public static AgentTask Embed(AgentTask task, PersistenceSecretRedactor? scope)
    {
        if (scope is null) return task;

        var found = scope.FoundIn(JsonSerializer.SerializeToElement(task, AgentJson.Options)).Where(secret => secret.Length >= AgentRunExecutor.MinimumNeedleLength);
        var secrets = (task.WorkflowSecrets ?? []).Concat(found).Distinct(StringComparer.Ordinal).ToArray();

        return secrets.Length == 0 ? task : task with { WorkflowSecrets = secrets };
    }

    /// <summary>The task as the store persists it: the masked <c>task_jsonb</c> and, when the task names secrets it embeds, the sealed exact copy. Both are the column-safe text (<see cref="PersistedText.SanitizeJson"/>), so the copy a run executes is the one its public row describes. While a previous-version worker is live the sealer writes the exact task unsealed instead (<see cref="WorkflowSensitivePayloadSealer"/>) — the row that worker launches from.</summary>
    public static SealedPayload Seal(AgentTask task, WorkflowSensitivePayloadSealer? sealer) => Persist(task, sealer, throughFleetGate: true);

    /// <summary>A task <see cref="Open"/>ed from its row, written back onto that row by the current worker running it: in the shape it was opened from — sealed again when it names secrets (only an opened sealed copy does), whatever the fleet gate says, else exact. A previous-version worker never launches a Running row, and the launch's redactor fingerprint names exactly these secrets, which a re-attach rebuilds from the row.</summary>
    public static SealedPayload Reseal(AgentTask task, WorkflowSensitivePayloadSealer? sealer) => Persist(task, sealer, throughFleetGate: false);

    /// <summary>The exact task a run executes — opened from its sealed copy, naming the secrets it embeds, when the row was sealed; else its <c>task_jsonb</c>, which is then itself exact. Null for an empty envelope.</summary>
    public static AgentTask? Open(AgentRun run, WorkflowSensitivePayloadSealer? sealer)
    {
        if (run.SealedTaskCiphertext is null) return JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options);

        var seal = sealer ?? throw new InvalidOperationException($"AgentRun {run.Id} holds a sealed task but no payload sealer is available to open it.");
        var opened = seal.Open(run.TaskJson, run.SealedTaskCiphertext);

        return JsonSerializer.Deserialize<AgentTask>(opened.Json, AgentJson.Options) is { } task ? task with { WorkflowSecrets = opened.Secrets } : null;
    }

    private static SealedPayload Persist(AgentTask task, WorkflowSensitivePayloadSealer? sealer, bool throughFleetGate)
    {
        var exact = PersistedText.SanitizeJson(JsonSerializer.Serialize(task, AgentJson.Options))!;

        if (task.WorkflowSecrets is not { Count: > 0 } secrets) return new SealedPayload(exact, null);

        var seal = sealer ?? throw new InvalidOperationException("An agent task embeds workflow secrets but no payload sealer is available to persist it masked.");

        using var document = JsonDocument.Parse(exact);
        var redactor = new PersistenceSecretRedactor(secrets);

        return throughFleetGate ? seal.Seal(document.RootElement, redactor) : seal.Reseal(document.RootElement, redactor);
    }
}
