using CodeSpace.Messages.Health;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// A tokened git command was refused for want of a credential on a host whose git is older than
/// <see cref="TokenedGitCommand.MinimumGit"/>: the helper that would have answered is configured through
/// <c>GIT_CONFIG_COUNT</c>, which that git ignores. Still a <see cref="WorkspaceException"/> — the run executor, the
/// integrate nodes and the acceptance grader already handle that type — and it keeps its failure code; what changes is that
/// the message names the cause and the fix, where git says only that it "could not read Username".
/// </summary>
public sealed class GitCredentialHelperUnsupportedException : WorkspaceException
{
    public GitCredentialHelperUnsupportedException(GitVersion version) : base(Describe(version))
    {
        Version = version;
    }

    /// <summary>The release of git this host runs.</summary>
    public GitVersion Version { get; }

    private static string Describe(GitVersion version) =>
        $"This host's git is {version}, older than {TokenedGitCommand.MinimumGit}. The credential helper that hands git its token is configured through GIT_CONFIG_COUNT, " +
        $"which git ignores before {TokenedGitCommand.MinimumGit}, so the remote's request for a credential went unanswered. Upgrade the git in this image to {TokenedGitCommand.MinimumGit} or later.";
}
