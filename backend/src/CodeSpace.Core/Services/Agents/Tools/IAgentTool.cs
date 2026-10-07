using System.Text.Json;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Tools;

/// <summary>
/// The ONE tool surface — the "Tool Fabric" contract every model-callable capability projects onto, so a
/// workflow node, an MCP server (for the CLI harnesses), and the future native loop all share a single tool
/// definition instead of three parallel implementations. A tool declares its schema + risk, validates input
/// purely, and executes to a structured result.
///
/// <para>Risk declarations are FAIL-CLOSED by default (a tool that forgets to declare itself read-only is treated
/// as a non-concurrency-safe, destructive, approval-requiring tool — so a forgotten flag can never sneak a write
/// into a parallel batch or past the approval gate). A tool overrides only the predicates it can safely relax.
/// The permission DECISION (allow / ask / deny) is the autonomy gate's job; the tool only DECLARES its risk.</para>
/// </summary>
public interface IAgentTool
{
    /// <summary>Stable invocation name (e.g. "git.open_pr", "run_command") — what the model calls and the registry keys on.</summary>
    string Kind { get; }

    /// <summary>One-line description shown to the model.</summary>
    string Description { get; }

    /// <summary>Alternate names this tool also answers to (migrations / ergonomics). Empty by default.</summary>
    IReadOnlyList<string> Aliases => Array.Empty<string>();

    /// <summary>Optional extra terms for tool-discovery search; null = none.</summary>
    string? SearchHint => null;

    /// <summary>JSON Schema for the tool's input arguments.</summary>
    JsonElement InputSchema { get; }

    /// <summary>JSON Schema for the tool's structured output.</summary>
    JsonElement OutputSchema { get; }

    /// <summary>True only if the call has no side effects (safe to run speculatively / cache). FAIL-CLOSED default: false.</summary>
    bool IsReadOnly => false;

    /// <summary>True only if the call is safe to run concurrently with other safe calls. FAIL-CLOSED default: false.</summary>
    bool IsConcurrencySafe => false;

    /// <summary>True if the call mutates the world irreversibly (push, merge, delete). FAIL-CLOSED default: true.</summary>
    bool IsDestructive => true;

    /// <summary>Whether this tool should be gated for approval by default. Defaults to <see cref="IsDestructive"/> — risky tools ask unless a tier opens them.</summary>
    bool RequiresApproval => IsDestructive;

    /// <summary>True for a tool that can NEVER run unattended — even at the Unleashed tier it requires a human approval (an
    /// irreversible op like a merge). The gate escalates Unleashed's Allow → RequireApproval when this is set; the other
    /// tiers are unchanged (Confined still denies, Standard/Trusted already ask). FAIL-CLOSED default: false.</summary>
    bool AlwaysRequiresApproval => false;

    /// <summary>Pure, I/O-free validation of the input shape/values (e.g. a blocked-path check) — the first gate, before any permission check or side effect.</summary>
    AgentToolValidation ValidateInput(JsonElement input);

    /// <summary>
    /// The refusal for a call this tool will not run whatever a human decides — judged on its arguments and its calling
    /// run (e.g. a repository outside the run's binding) — or null to admit it. Consulted before an approval-gated call is
    /// parked, so no human is asked to approve a call that would only be refused; <see cref="CallAsync"/> still enforces
    /// it, since what it judges can change while a card waits. Default: admit.
    /// </summary>
    Task<string?> RefusalAsync(AgentToolCall call, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>
    /// What this call will do, resolved server-side for the human asked to approve it — the lines its approval card shows,
    /// the target a rejection sticks to, and any input pinned to what the card showed. Consulted only for a call the gate
    /// parks for approval, after <see cref="RefusalAsync"/> admitted it; the MCP handler redacts and bounds it. Throws
    /// <c>ToolCallPreviewException</c> when what the card must show cannot be read, so the call is answered instead of
    /// parked. Default: the arguments as given, the whole call its target.
    /// </summary>
    Task<ToolCallPreview> PreviewAsync(AgentToolCall call, CancellationToken cancellationToken) => Task.FromResult(ToolCallPreviews.FromArguments(call.Input));

    /// <summary>Execute the (already-validated, already-permitted) call to a structured result. Errors come back as a typed <see cref="AgentToolResult"/>, not a thrown exception.</summary>
    Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken);
}
