using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="RemoteTipResolver"/> against a REAL local git repo (the <see cref="LocalGitWorkspaceProviderTests"/>
/// pattern) — the resolver reads tips over the same git transport the clone uses, so the tests prove the pin can
/// never skew from what the clone would fetch. Skips where git isn't installed (cross-host <c>dotnet test</c> stays clean).
/// </summary>
[Trait("Category", "Unit")]
public sealed class RemoteTipResolverTests
{
    [Fact]
    public async Task Resolves_a_named_branch_to_its_tip_commit()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "v1");
        var expected = await GitStdoutAsync(origin.Path, "rev-parse", "HEAD");

        var sha = await NewResolver().ResolveTipShaAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "main" }, refRequired: true, CancellationToken.None);

        sha.ShouldBe(expected);
    }

    [Fact]
    public async Task Resolves_HEAD_when_no_ref_is_named()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "v1");
        var expected = await GitStdoutAsync(origin.Path, "rev-parse", "HEAD");

        var sha = await NewResolver().ResolveTipShaAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }, refRequired: true, CancellationToken.None);

        sha.ShouldBe(expected, "no ref ⇒ the remote's HEAD — the same commit a bare `git clone` would materialize");
    }

    [Fact]
    public async Task A_missing_HARD_ref_fails_loud_naming_the_ref()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "v1");

        var ex = await Should.ThrowAsync<WorkspaceException>(() => NewResolver().ResolveTipShaAsync(
            new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "release/9.x" }, refRequired: true, CancellationToken.None));

        ex.Message.ShouldContain("release/9.x", customMessage: "a HARD ref that is gone fails the launch loud — the clone would fail identically later, never a silent unpinned launch");
    }

    [Fact]
    public async Task A_missing_SOFT_ref_falls_back_to_the_default_ref_tip()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "v1");
        var expected = await GitStdoutAsync(origin.Path, "rev-parse", "HEAD");

        // DefaultRef set = the request's own SOFT semantics (a pruned session branch degrades to the default) —
        // the resolver mirrors the clone's ResolveCheckoutRefAsync so pin and clone can never diverge.
        var sha = await NewResolver().ResolveTipShaAsync(
            new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "run-1/pruned", DefaultRef = "main" }, refRequired: true, CancellationToken.None);

        sha.ShouldBe(expected);
    }

    [Fact]
    public async Task A_tag_resolves_to_its_commit()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "v1");
        var expected = await GitStdoutAsync(origin.Path, "rev-parse", "HEAD");
        await RunGitAsync(origin.Path, "tag", "-a", "v1.0", "-m", "release");   // annotated: the tag OBJECT sha ≠ the commit sha
        await WriteAndCommitAsync(origin.Path, "file.txt", "v2");               // the tip moves past the tag

        var sha = await NewResolver().ResolveTipShaAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "v1.0" }, refRequired: true, CancellationToken.None);

        sha.ShouldBe(expected, "an annotated tag pin must be the PEELED commit — the tag object itself is not a tree the workspace can materialize");
    }

    [Fact]
    public async Task An_empty_remote_returns_null_nothing_exists_to_pin()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await RunGitAsync(origin.Path, "init", "--bare", "-b", "main");

        var sha = await NewResolver().ResolveTipShaAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }, refRequired: true, CancellationToken.None);

        sha.ShouldBeNull("an empty remote has no commit to pin — the launch proceeds unpinned rather than failing a brand-new repo");
    }

    [Fact]
    public async Task An_unreachable_remote_fails_loud()
    {
        if (!await GitAvailableAsync()) return;

        var dead = Path.Combine(Path.GetTempPath(), "cs-no-such-remote-" + Guid.NewGuid().ToString("N"));

        await Should.ThrowAsync<WorkspaceException>(() => NewResolver().ResolveTipShaAsync(
            new WorkspaceRequest { RepositoryUrl = new Uri(dead).AbsoluteUri, Ref = "main" }, refRequired: true, CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_IMPLICIT_ref_returns_null_instead_of_throwing()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await RunGitAsync(origin.Path, "init", "--bare", "-b", "main");   // an empty just-created repo: its recorded default branch has no commits yet

        var sha = await NewResolver().ResolveTipShaAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "main" }, refRequired: false, CancellationToken.None);

        sha.ShouldBeNull("an IMPLICIT recorded default the remote doesn't have launches UNPINNED (the pre-S1 behaviour) — an opportunistic pin must never fail a brand-new repo's launch");
    }

    [Fact]
    public async Task A_branch_is_preferred_over_a_tag_with_the_same_name()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "v1");
        await RunGitAsync(origin.Path, "tag", "release");                  // a tag at v1…
        var branchTip = await WriteCommitAndBranchAsync(origin.Path);      // …and a BRANCH `release` at a different commit

        var sha = await NewResolver().ResolveTipShaAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "release" }, refRequired: true, CancellationToken.None);

        sha.ShouldBe(branchTip, "refs/heads wins over refs/tags for an ambiguous name — matching git clone --branch's own preference");
    }

    [Fact]
    public async Task A_lightweight_tag_resolves_to_its_commit()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "v1");
        var expected = await GitStdoutAsync(origin.Path, "rev-parse", "HEAD");
        await RunGitAsync(origin.Path, "tag", "v1.0");                     // lightweight: no tag object, no peeled ^{} line
        await WriteAndCommitAsync(origin.Path, "file.txt", "v2");

        var sha = await NewResolver().ResolveTipShaAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "v1.0" }, refRequired: true, CancellationToken.None);

        sha.ShouldBe(expected, "a lightweight tag points at the commit directly — the non-peeled fallback arm must return it");
    }

    [Fact]
    public async Task A_missing_soft_ref_whose_default_is_also_missing_fails_loud()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "v1");

        await Should.ThrowAsync<WorkspaceException>(() => NewResolver().ResolveTipShaAsync(
            new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "run-1/pruned", DefaultRef = "no-such-default" }, refRequired: true, CancellationToken.None));
    }

    [Fact]
    public void SanitizeUrl_strips_userinfo_and_leaves_clean_urls_alone()
    {
        RemoteTipResolver.SanitizeUrl("https://user:secret@host/repo.git").ShouldNotContain("secret", customMessage: "a stored clone URL may itself carry credentials the token redaction knows nothing about");
        RemoteTipResolver.SanitizeUrl("https://host/repo.git").ShouldBe("https://host/repo.git");
    }

    // ─── harness (the LocalGitWorkspaceProviderTests pattern) ───────────────────────

    private static RemoteTipResolver NewResolver() =>
        new(new SandboxRunnerRegistry(new ISandboxRunner[] { new LocalProcessRunner() }));

    private static string AsFileUrl(string path) => new Uri(path).AbsoluteUri;

    private static async Task<bool> GitAvailableAsync()
    {
        try
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None);
            return result.Status == SandboxStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private static async Task SeedOriginAsync(string dir, string file, string content)
    {
        await RunGitAsync(dir, "init", "-b", "main");
        await RunGitAsync(dir, "config", "user.email", "test@codespace.dev");
        await RunGitAsync(dir, "config", "user.name", "Test");
        await RunGitAsync(dir, "config", "commit.gpgsign", "false");
        await WriteAndCommitAsync(dir, file, content);
    }

    private static async Task WriteAndCommitAsync(string dir, string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(dir, file), content);
        await RunGitAsync(dir, "add", ".");
        await RunGitAsync(dir, "commit", "-m", "seed");
    }

    /// <summary>Commit on a new branch named <c>release</c> (shadowing the tag of the same name), back to main; returns the branch tip.</summary>
    private static async Task<string> WriteCommitAndBranchAsync(string dir)
    {
        await RunGitAsync(dir, "checkout", "-b", "release");
        await WriteAndCommitAsync(dir, "file.txt", "branch-content");
        var tip = await GitStdoutAsync(dir, "rev-parse", "HEAD");
        await RunGitAsync(dir, "checkout", "main");
        return tip;
    }

    private static async Task RunGitAsync(string workdir, params string[] args)
    {
        var result = await new LocalProcessRunner().RunAsync(
            new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

        if (result.Status != SandboxStatus.Success)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    private static async Task<string> GitStdoutAsync(string workdir, params string[] args)
    {
        var result = await new LocalProcessRunner().RunAsync(
            new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

        if (result.Status != SandboxStatus.Success)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

        return result.Stdout.Trim();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-tip-origin-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
