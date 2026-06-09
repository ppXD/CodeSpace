using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="LocalGitWorkspaceProvider"/> — the pure auth-URL builder (no git), plus the real clone
/// mechanics against a REAL local git repo (mirrors <see cref="LocalProcessRunnerTests"/> driving a real
/// process). The clone tests skip where git isn't installed, so cross-host <c>dotnet test</c> stays clean.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalGitWorkspaceProviderTests
{
    // ─── Pure auth-URL builder ───────────────────────────────────────────────

    [Fact]
    public void No_token_leaves_the_url_unchanged() =>
        LocalGitWorkspaceProvider.BuildAuthenticatedUrl("https://github.com/org/repo.git", null, null)
            .ShouldBe("https://github.com/org/repo.git");

    [Fact]
    public void Token_with_no_username_defaults_to_x_access_token() =>
        LocalGitWorkspaceProvider.BuildAuthenticatedUrl("https://github.com/org/repo.git", null, "ghp_abc")
            .ShouldBe("https://x-access-token:ghp_abc@github.com/org/repo.git");

    [Fact]
    public void Token_uses_the_provider_specific_username() =>
        LocalGitWorkspaceProvider.BuildAuthenticatedUrl("https://gitlab.com/org/repo.git", "oauth2", "glpat_xyz")
            .ShouldBe("https://oauth2:glpat_xyz@gitlab.com/org/repo.git");

    [Fact]
    public void Special_characters_in_the_token_are_escaped() =>
        LocalGitWorkspaceProvider.BuildAuthenticatedUrl("https://example.com/r.git", "u", "p@ss/word")
            .ShouldBe("https://u:p%40ss%2Fword@example.com/r.git");

    [Fact]
    public void Authenticated_url_preserves_a_non_default_port() =>
        LocalGitWorkspaceProvider.BuildAuthenticatedUrl("https://git.local:8443/org/repo.git", "oauth2", "t")
            .ShouldBe("https://oauth2:t@git.local:8443/org/repo.git");

    // ─── Real clone mechanics ────────────────────────────────────────────────

    [Fact]
    public async Task Clones_into_an_isolated_directory_and_cleans_up_on_dispose()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "hello-agent");

        var handle = await NewProvider().PrepareAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }, CancellationToken.None);

        string dir;
        await using (handle)
        {
            dir = handle.Directory;
            Directory.Exists(dir).ShouldBeTrue();
            Directory.Exists(Path.Combine(dir, ".git")).ShouldBeTrue("the workspace is a git working copy");
            (await File.ReadAllTextAsync(Path.Combine(dir, "README.md"))).Trim().ShouldBe("hello-agent");
        }

        Directory.Exists(dir).ShouldBeFalse("DisposeAsync removes the workspace directory");
    }

    [Fact]
    public async Task Checks_out_the_requested_ref()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "main-content");
        await RunGitAsync(origin.Path, "checkout", "-b", "feature");
        await WriteAndCommitAsync(origin.Path, "feature.txt", "feature-content");

        await using var handle = await NewProvider().PrepareAsync(
            new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "feature" }, CancellationToken.None);

        File.Exists(Path.Combine(handle.Directory, "feature.txt")).ShouldBeTrue("the requested branch is checked out");
    }

    [Fact]
    public async Task Does_not_persist_credentials_in_git_config()
    {
        // Token auth against a local origin that ignores it — the point is the post-clone remote rewrite:
        // .git/config must not retain the embedded credentials.
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "x");

        await using var handle = await NewProvider().PrepareAsync(
            new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Token = "secret-token", TokenUsername = "x-access-token" }, CancellationToken.None);

        var config = await File.ReadAllTextAsync(Path.Combine(handle.Directory, ".git", "config"));
        config.ShouldNotContain("secret-token", Case.Insensitive, "the token must be stripped from the persisted remote");
    }

    [Fact]
    public async Task Failed_clone_throws_a_workspace_exception()
    {
        if (!await GitAvailableAsync()) return;

        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));

        await Should.ThrowAsync<WorkspaceException>(async () =>
            await NewProvider().PrepareAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(missing) }, CancellationToken.None));
    }

    [Fact]
    public void Kind_is_local() => NewProvider().Kind.ShouldBe("local");

    // ─── Change capture ──────────────────────────────────────────────────────

    [Fact]
    public async Task Captures_edits_new_files_and_deletions_as_a_diff()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "keep.txt", "original");
        await WriteAndCommitAsync(origin.Path, "remove.txt", "to be deleted");

        await using var handle = await NewProvider().PrepareAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }, CancellationToken.None);

        // The "agent" edits a tracked file, adds a new one, and deletes another.
        await File.WriteAllTextAsync(Path.Combine(handle.Directory, "keep.txt"), "edited by agent");
        await File.WriteAllTextAsync(Path.Combine(handle.Directory, "new.txt"), "brand new");
        File.Delete(Path.Combine(handle.Directory, "remove.txt"));

        var changes = await handle.CaptureChangesAsync(CancellationToken.None);

        changes.IsEmpty.ShouldBeFalse();
        changes.ChangedFiles.ShouldBe(new[] { "keep.txt", "new.txt", "remove.txt" }, ignoreOrder: true);
        changes.Patch.ShouldContain("edited by agent");
        changes.Patch.ShouldContain("brand new");
    }

    [Fact]
    public async Task Captures_nothing_when_the_agent_made_no_changes()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "unchanged");

        await using var handle = await NewProvider().PrepareAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }, CancellationToken.None);

        var changes = await handle.CaptureChangesAsync(CancellationToken.None);

        changes.IsEmpty.ShouldBeTrue();
        changes.ChangedFiles.ShouldBeEmpty();
        changes.Patch.ShouldBeEmpty();
    }

    [Fact]
    public async Task Captures_committed_work_against_the_cloned_base()
    {
        // If the agent commits (HEAD moves), the diff is still taken vs the cloned base — committed work is captured.
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "base");

        await using var handle = await NewProvider().PrepareAsync(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }, CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(handle.Directory, "feature.txt"), "committed change");
        await RunGitAsync(handle.Directory, "config", "user.email", "agent@codespace.dev");
        await RunGitAsync(handle.Directory, "config", "user.name", "Agent");
        await RunGitAsync(handle.Directory, "config", "commit.gpgsign", "false");
        await RunGitAsync(handle.Directory, "add", ".");
        await RunGitAsync(handle.Directory, "commit", "-m", "agent commit");

        var changes = await handle.CaptureChangesAsync(CancellationToken.None);

        changes.ChangedFiles.ShouldContain("feature.txt", "committed work is captured vs the base");
        changes.Patch.ShouldContain("committed change");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static LocalGitWorkspaceProvider NewProvider() =>
        new(new SandboxRunnerRegistry(new ISandboxRunner[] { new LocalProcessRunner() }), NullLogger<LocalGitWorkspaceProvider>.Instance);

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

    private static async Task RunGitAsync(string workdir, params string[] args)
    {
        var result = await new LocalProcessRunner().RunAsync(
            new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

        if (result.Status != SandboxStatus.Success)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-origin-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
