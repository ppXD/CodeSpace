using CodeSpace.Messages.Agents;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Cross-platform <see cref="SandboxSpec"/> builders for the runner contract tests — the scenarios every
/// <c>ISandboxRunner</c> must handle identically (success, exit code, stderr, env, cwd, timeout, cancel,
/// line streaming). Assumes a shell-capable execution environment, which every sandbox runner is by
/// definition; a future non-shell runner would supply its own specs.
/// </summary>
internal static class ContractSpecs
{
    public static SandboxSpec Print(string text) => Shell(Win ? $"echo {text}" : $"printf '%s\\n' '{text}'");
    public static SandboxSpec PrintToStderr(string text) => Shell(Win ? $"echo {text} 1>&2" : $"printf '%s\\n' '{text}' 1>&2");
    public static SandboxSpec ExitWith(int code) => Shell($"exit {code}");
    public static SandboxSpec Sleep(int seconds) => Shell(Win ? $"ping -n {seconds + 1} 127.0.0.1 >nul" : $"sleep {seconds}");
    public static SandboxSpec PrintEnvVar(string name) => Shell(Win ? $"echo %{name}%" : $"printf '%s\\n' \"${name}\"");
    public static SandboxSpec PrintWorkingDirectory() => Shell(Win ? "cd" : "pwd");
    public static SandboxSpec MultiLine(params string[] lines) => Shell(Win ? string.Join("& ", lines.Select(l => $"echo {l}")) : "printf '" + string.Join("\\n", lines) + "\\n'");
    public static SandboxSpec PrintThenExit(string text, int code) => Shell(Win ? $"echo {text}& exit {code}" : $"printf '%s\\n' '{text}'; exit {code}");

    private static bool Win => OperatingSystem.IsWindows();

    private static SandboxSpec Shell(string script) => new()
    {
        Command = Win ? "cmd.exe" : "/bin/sh",
        Args = Win ? new[] { "/c", script } : new[] { "-c", script },
        TimeoutSeconds = 30,
    };
}
