namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Slice B1 — the harness ADAPTER CONTRACT (instruction half): the operating directive every harness injects into the
/// agent's system prompt, so the model is TOLD the rules of an unattended run rather than discovering them by hanging.
/// It reinforces, at the instruction layer, the mechanical floors shipped elsewhere: the non-interactive env (C1) — do
/// not wait on stdin; the completion contract (A1/A2) — do not end the turn by asking. The model's COOPERATION can't be
/// code-forced (that's the human-gated real-model quality gate); the completion contract is the backstop when it
/// ignores this. The text is GENERIC + harness-agnostic — each harness projects it through its own mechanism (Claude
/// Code: <c>--append-system-prompt</c>; Codex exec has no native system-prompt flag, so it is prepended to the prompt).
/// Always injected — it is valid for every unattended run, and the "when a decision tool is available" phrasing keeps it
/// correct whether or not the MCP fabric is wired. Pinned by a test so the contract text is a reviewed, stable surface.
/// </summary>
public static class AgentOperatingContract
{
    /// <summary>
    /// B1: the full system-prompt text a harness projects natively — the agent's PERSONA (if any) followed by the
    /// always-on <see cref="SystemDirective"/> operating contract, blank-line separated. A null/blank persona yields the
    /// bare contract (byte-identical to a pre-persona run). The contract goes LAST so it can't be diluted by the persona,
    /// and both live in the SYSTEM prompt (not the user goal) per Anthropic's guidance. Both harnesses call this and route
    /// the result to their own channel (Claude <c>--append-system-prompt</c>; Codex config-home <c>AGENTS.md</c>).
    /// </summary>
    public static string Compose(string? persona)
    {
        var p = (persona ?? string.Empty).Trim();

        return p.Length == 0 ? SystemDirective : p + "\n\n" + SystemDirective;
    }

    /// <summary>The operating directive injected into the harness system prompt. Stable wording — a model relies on it, and tests pin it.</summary>
    public const string SystemDirective =
        "You are an automated, UNATTENDED agent running in a sandbox — no human is at the terminal to answer prompts. Operate accordingly:\n" +
        "- Do NOT run interactive commands or wait on stdin. Pass non-interactive flags (e.g. -y / --yes); if a tool would still prompt, take the safe default or skip it rather than blocking.\n" +
        "- If you hit a decision you genuinely cannot make yourself — a yes/no, or a choice between alternatives — raise it with the decision tool when one is available, instead of guessing silently or stopping to ask.\n" +
        "- Do NOT end your turn by asking the operator a question. Either complete the task or clearly state what blocked you; a run that ends on an unanswered question is treated as INCOMPLETE, not done.";
}
