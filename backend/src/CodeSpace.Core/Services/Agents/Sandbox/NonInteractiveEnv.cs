namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// Slice C1 — the non-interactive environment a SCRUBBED agent child always starts with, so a nested package / VCS tool
/// that would otherwise PROMPT ("Continue? [Y/n]", an apt debconf question, a git credential prompt) instead takes its
/// non-interactive default. An unattended agent has no human at the terminal: without these, such a prompt reads EOF or
/// blocks until the run's wall-clock timeout kills the whole tree — surfacing a useless <c>TimedOut</c> rather than
/// progress, and never a decide. These are the widely-respected "I am in CI / do not prompt" signals for the common
/// toolchains (apt/dpkg, npm/npx, pip, git); a tool that ignores them is the later PTY safety-net's job, not this bypass.
///
/// <para>Injected as the BASE of the scrubbed env — an allow-listed inherited value, then an explicit
/// <c>SandboxSpec.Environment</c> value, layered on top still WINS (operator intent overrides the default), so a task
/// can deliberately set e.g. <c>DEBIAN_FRONTEND=dialog</c>. Runner-agnostic (it sits at the sandbox concern root so any
/// runner injects the same set); pinned by a unit test so the set is a visible, reviewed decision, not silent drift.</para>
/// </summary>
public static class NonInteractiveEnv
{
    /// <summary>The non-interactive defaults, by name. Ordinal-keyed (env names are case-sensitive on the platforms that matter here).</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CI"] = "1",                              // the universal "running unattended, do not prompt" signal many tools honour
        ["DEBIAN_FRONTEND"] = "noninteractive",    // apt / dpkg / debconf take defaults, never prompt
        ["DEBCONF_NONINTERACTIVE_SEEN"] = "true",  //   companion: treat debconf questions as already seen (some dpkg paths still prompt without it)
        ["NPM_CONFIG_YES"] = "true",               // npm / npx auto-confirm (e.g. npx auto-installs a missing package)
        ["PIP_NO_INPUT"] = "1",                    // pip never prompts
        ["GIT_TERMINAL_PROMPT"] = "0",             // git fails fast instead of blocking on a credential prompt
    };
}
