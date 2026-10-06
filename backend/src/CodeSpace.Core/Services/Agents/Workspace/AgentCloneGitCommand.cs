using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Builds every <c>git</c> spec the platform runs over an agent's clone after the agent's turn: the capture
/// (<c>add -A</c> and the <c>diff --cached</c> trio), the re-attach capture, and the branch steps (<c>checkout -B</c>,
/// <c>add -A</c>, <c>commit</c>, <c>diff --quiet</c>, <c>rev-parse HEAD</c>). The agent can write its own <c>.git</c>
/// during the run, so by then its config, hooks and attributes are the agent's. Each spec built here makes sure a
/// tampered <c>.git</c> cannot run code with anything the agent does not already have:
/// <list type="bullet">
///   <item><c>-c core.hooksPath=/dev/null</c> — a command-line value beats the repo's own, so neither a planted
///   <c>.git/hooks/*</c> nor an agent-set hooks directory runs, and a vetoing pre-commit can no longer lose the
///   produced branch. git looks hooks up as <c>/dev/null/&lt;name&gt;</c>, which can never exist. Not an empty value:
///   git resolves <c>core.hooksPath=</c> to the filesystem root.</item>
///   <item><c>-c core.fsmonitor=</c> — the empty value turns off a repo <c>core.fsmonitor</c> program on every git
///   version. Not <c>false</c>: before 2.36 git reads the key only as a program path and runs <c>false</c> from PATH on
///   every index refresh.</item>
///   <item>a diff gets <c>--no-ext-diff --no-textconv</c>, so <c>diff.external</c>, <c>diff.&lt;driver&gt;.command</c>
///   and <c>diff.&lt;driver&gt;.textconv</c> (selected through <c>.gitattributes</c>) never run.</item>
///   <item>no network and no credential: <see cref="SandboxSpec.AllowNetwork"/> is false and the environment is empty.
///   A repo <c>filter.&lt;driver&gt;.clean</c> still runs on <c>add -A</c> — git has no command-line switch for it —
///   but only at the agent's own uid, with no secret and, under a confining runner, no egress.</item>
/// </list>
/// The <c>-c</c> values reach child git processes too (a commit's <c>gc --auto</c>), through
/// <c>GIT_CONFIG_PARAMETERS</c>. Global and system config are left alone: the worker image registers git-lfs with
/// <c>git lfs install --system</c>, and operators may set a CA bundle or proxy globally. Commands that reach the remote
/// (clone, fetch, ls-remote, the authenticated push) are not built here.
/// </summary>
internal static class AgentCloneGitCommand
{
    /// <summary>The config every command gets ahead of its own arguments.</summary>
    internal static readonly IReadOnlyList<string> HardeningConfig = new[] { "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=" };

    /// <summary>The flags a <c>diff</c> gets right after the subcommand.</summary>
    private static readonly IReadOnlyList<string> DiffDriverSuppression = new[] { "--no-ext-diff", "--no-textconv" };

    /// <summary>The hardened spec for one git command in an agent clone; <paramref name="args"/> is the command as written without hardening, e.g. <c>["add","-A"]</c> or a <c>commit</c> with its inline <c>-c</c> identity flags.</summary>
    internal static SandboxSpec Build(IReadOnlyList<string> args, string workingDirectory, IReadOnlyList<string> readOnlyPaths, int timeoutSeconds) => new()
    {
        Command = "git",
        Args = HardenedArgs(args),
        WorkingDirectory = workingDirectory,
        ReadOnlyPaths = readOnlyPaths,
        TimeoutSeconds = timeoutSeconds,
        AllowNetwork = false,
    };

    /// <summary><see cref="HardeningConfig"/> ahead of the command, plus <see cref="DiffDriverSuppression"/> right after a leading <c>diff</c>.</summary>
    internal static IReadOnlyList<string> HardenedArgs(IReadOnlyList<string> args) =>
        IsDiff(args) ? [.. HardeningConfig, args[0], .. DiffDriverSuppression, .. args.Skip(1)] : [.. HardeningConfig, .. args];

    private static bool IsDiff(IReadOnlyList<string> args) => args.Count > 0 && args[0] == "diff";
}
