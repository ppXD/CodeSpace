using System.Text.RegularExpressions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// An INVENTORY of every place a harness double builds its own result instead of going through
/// <see cref="ScriptedFolders"/>.
///
/// <para><c>AgentFolderDoubleFidelityTests</c> proves the SHARED construction reports everything production reports.
/// It can say nothing about a double that builds its result inline — and most of this suite still does, which means
/// most of this suite is still blind in exactly the way that cost three review rounds: a fixture reporting LESS than
/// production breaks nothing except a test's ability to notice a regression.</para>
///
/// <para>So this does not forbid the inline shape; it refuses to let the set of them grow by accident. Every site that
/// exists today is listed below with the file it lives in. A new one reddens this test, and the author then chooses:
/// route it through <see cref="ScriptedFolders"/> (almost always right), or add the file here deliberately.</para>
///
/// <para>Deliberately a SOURCE scan rather than reflection: the thing being counted is a syntactic construction, and a
/// compiled lambda cannot be asked which fields it forgot.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScriptedFolderInventoryTests
{
    /// <summary>
    /// Test files that still build folder results inline, each a known blind spot rather than a decision. They are not
    /// converted here because this PR's subject is the lost-lease landing, and rewriting nine unrelated suites' doubles
    /// inside it would bury the change under a refactor — the PR body lists them as follow-up. The two files this PR
    /// DID touch are deliberately absent: they are routed through <see cref="ScriptedFolders"/>.
    /// </summary>
    private static readonly IReadOnlySet<string> InlineFolderSites = new HashSet<string>(StringComparer.Ordinal)
    {
        "AgentMcpEndpointFlowTests.cs",
        "AgentRunReconcileRepairFlowTests.cs",
        "DeclaredDeliverablePromiseFlowTests.cs",
        "HarnessReductionReattachFlowTests.cs",
        "LocalAcceptanceExecutorFlowTests.cs",
        "NativeRecordDualWriteFlowTests.cs",
        "ScratchWorkspaceFlowTests.cs",
        "AgentBranchPushFlowTests.cs",
        "AgentReviewerLoopFlowTests.cs",
        "AgentRunExecutorTests.cs",
        "AgentRunReviseLoopFlowTests.cs",
        "AgentUnderClaimGradeFlowTests.cs",
        "PublishGuardChainFlowTests.cs",
        "SupervisorDependencyStagingFlowTests.cs",
        "SupervisorRetryWorldStateFlowTests.cs",
    };

    [Fact]
    public void No_NEW_harness_double_builds_its_result_outside_the_shared_construction()
    {
        var root = RepositoryTestRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "backend", "tests", "CodeSpace.IntegrationTests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "ScriptedFolders.cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"new\s+TestEventFolder\s*\("))
            .Select(Path.GetFileName)
            .Where(f => !InlineFolderSites.Contains(f!))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "these build a folder result inline, so AgentFolderDoubleFidelityTests cannot see what they drop — and a double that " +
            "reports less than production is invisible until a review finds it. Use ScriptedFolders.Result(), or add the file to " +
            "InlineFolderSites with a reason:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_inventory_does_not_rot()
    {
        var root = Path.Combine(RepositoryTestRoot(), "backend", "tests", "CodeSpace.IntegrationTests");
        var live = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"new\s+TestEventFolder\s*\("))
            .Select(f => Path.GetFileName(f)!)
            .ToHashSet(StringComparer.Ordinal);

        var converted = InlineFolderSites.Where(f => !live.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();

        converted.ShouldBeEmpty(
            "these are listed as inline-folder sites but no longer build one — delete their lines, so the list keeps naming " +
            "real blind spots rather than becoming a stale exemption nobody rereads:\n  " + string.Join("\n  ", converted));
    }

    /// <summary>The repository root, walked up from the test assembly's location — the suite already runs from bin/, and this needs the SOURCE.</summary>
    private static string RepositoryTestRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend", "tests")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root from the test assembly location.");
    }
}
