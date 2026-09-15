using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests;

/// <summary>
/// The ONE folder construction every scripted harness double in this suite uses.
///
/// <para>It exists so <c>AgentFolderDoubleFidelityTests</c> can measure the real thing. When each double built its own
/// result inline, they drifted apart and away from production independently — two review rounds were spent on
/// landings that recorded no session id and no token spend, and no test could see it, because a fixture that reports
/// LESS than production breaks nothing except a test's ability to notice a regression.</para>
///
/// <para>It carries the same facts <c>ClaudeCodeResultFolder</c> and <c>CodexResultFolder</c> carry — the executor
/// hands every folder its accumulated <see cref="AgentRunFacts"/> at <c>BuildResult</c>, and a folder that drops them
/// is simply reporting less about the run than it was told. Values may differ from production (the summary and exit
/// reasons here are a double's own); which facts reach the result at all may not.</para>
/// </summary>
internal static class ScriptedFolders
{
    /// <summary>The default shape: exit 0 succeeds with the last line as its summary, anything else fails naming the code.</summary>
    internal static IAgentEventFolder Result() => Result((fold, exitCode) => exitCode == 0
        ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = fold.LastText }
        : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" });

    /// <summary>A double that decides its own status/summary but still reports every fact production reports. <paramref name="verdict"/> supplies only what is genuinely the double's business.</summary>
    internal static IAgentEventFolder Result(Func<TestEventFolder, int, AgentRunResult> verdict) =>
        new TestEventFolder((fold, exitCode) => WithFoldedFacts(verdict(fold, exitCode), fold));

    /// <summary>
    /// Attach everything the fold established, exactly as the two production folders do — the run's session id, its
    /// token spend, the model the CLI named, and the distinct changed files the stream reported. A verdict that
    /// already decided one of these keeps it.
    /// </summary>
    private static AgentRunResult WithFoldedFacts(AgentRunResult verdict, TestEventFolder fold) => verdict with
    {
        SessionId = verdict.SessionId is { Length: > 0 } ? verdict.SessionId : fold.SessionId,
        Model = verdict.Model is { Length: > 0 } ? verdict.Model : fold.Model,
        TokenUsage = verdict.TokenUsage ?? fold.TokenUsage,
        ChangedFiles = verdict.ChangedFiles is { Count: > 0 } ? verdict.ChangedFiles : fold.ChangedFiles,
    };
}
