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
[Collection("WorkspaceProvisioning")]   // the cleanup-leak test counts the process-global WorkspacesRoot — serialize it against parallel workspace-creators
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

    [Fact]
    public void Redact_scrubs_both_the_raw_token_and_its_url_encoded_form()
    {
        // The push argv embeds Uri.EscapeDataString(token); a token with URL-special chars appears ENCODED in a
        // failing push command, so redacting only the raw literal would leak the reversible encoded form.
        const string token = "p@ss/w+rd=secret";
        var leak = $"git push https://x-access-token:{Uri.EscapeDataString(token)}@host/r.git refused; raw {token} too";

        var redacted = LocalGitWorkspaceProvider.Redact(leak, token);

        redacted.ShouldNotContain(token, Case.Insensitive, "the raw token literal must be scrubbed");
        redacted.ShouldNotContain(Uri.EscapeDataString(token), Case.Insensitive, "the percent-encoded token (as it appears in the push argv) must ALSO be scrubbed");
        redacted.ShouldContain("***");
    }

    [Fact]
    public void Redact_is_a_noop_without_a_token() =>
        LocalGitWorkspaceProvider.Redact("nothing to hide here", null).ShouldBe("nothing to hide here");

    // ─── Shared token-strip helper (also reused by SupervisorAcceptanceGrader.CloneAtBaseAsync — one
    //     implementation for a security-sensitive path, so its own fallback ladder is pinned directly here
    //     rather than only indirectly through the full clone flow, which never exercises a set-url FAILURE) ───

    [Fact]
    public async Task StripToken_succeeds_via_set_url_without_touching_the_remote_further()
    {
        var runner = new ScriptedStripRunner(setUrlSucceeds: true, removeSucceeds: true);

        await LocalGitWorkspaceProvider.StripTokenFromRemoteAsync(runner, 60, NullLogger<LocalGitWorkspaceProvider>.Instance, "https://host/r.git", "/tmp/x", CancellationToken.None);

        runner.Invocations.Count.ShouldBe(1, "a successful set-url never falls through to remove — the happy path issues exactly one git call");
        runner.Invocations[0].Args.ShouldBe(new[] { "-C", "/tmp/x", "remote", "set-url", "origin", "https://host/r.git" });
    }

    [Fact]
    public async Task StripToken_falls_back_to_removing_the_remote_when_set_url_fails()
    {
        var runner = new ScriptedStripRunner(setUrlSucceeds: false, removeSucceeds: true);

        await LocalGitWorkspaceProvider.StripTokenFromRemoteAsync(runner, 60, NullLogger<LocalGitWorkspaceProvider>.Instance, "https://host/r.git", "/tmp/x", CancellationToken.None);

        runner.Invocations.Count.ShouldBe(2, "set-url failing must trigger exactly one fallback attempt");
        runner.Invocations[1].Args.ShouldBe(new[] { "-C", "/tmp/x", "remote", "remove", "origin" });
    }

    [Fact]
    public async Task StripToken_never_throws_even_when_both_set_url_and_remove_fail()
    {
        // The clone already succeeded by the time this runs — a credential-leak we FAILED to close must never
        // fail the grade/workspace-prep outright (the janitor is the documented final backstop).
        var runner = new ScriptedStripRunner(setUrlSucceeds: false, removeSucceeds: false);

        await Should.NotThrowAsync(() =>
            LocalGitWorkspaceProvider.StripTokenFromRemoteAsync(runner, 60, NullLogger<LocalGitWorkspaceProvider>.Instance, "https://host/r.git", "/tmp/x", CancellationToken.None));

        runner.Invocations.Count.ShouldBe(2);
    }

    private sealed class ScriptedStripRunner : ISandboxRunner
    {
        private readonly bool _setUrlSucceeds;
        private readonly bool _removeSucceeds;

        public ScriptedStripRunner(bool setUrlSucceeds, bool removeSucceeds) { _setUrlSucceeds = setUrlSucceeds; _removeSucceeds = removeSucceeds; }

        public string Kind => "local";
        public List<SandboxSpec> Invocations { get; } = new();

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Invocations.Add(spec);

            var succeeds = spec.Args.Contains("set-url") ? _setUrlSucceeds : _removeSucceeds;

            return Task.FromResult(succeeds
                ? new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" }
                : new SandboxResult { Status = SandboxStatus.Failed, ExitCode = 1, Stdout = "", Stderr = "git error" });
        }
    }

    // ─── Real clone mechanics ────────────────────────────────────────────────

    [Fact]
    public async Task Clones_into_an_isolated_directory_and_cleans_up_on_dispose()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "hello-agent");

        var handle = await NewProvider().PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }), CancellationToken.None);

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
            WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "feature" }), CancellationToken.None);

        File.Exists(Path.Combine(handle.Directory, "feature.txt")).ShouldBeTrue("the requested branch is checked out");
    }

    [Fact]
    public async Task A_present_soft_ref_is_checked_out_unchanged()
    {
        // A SOFT ref (DefaultRef set — a session-inherited prior branch) that STILL EXISTS is checked out exactly as
        // before: the existence pre-flight passes, so there is no fallback. Proves the happy path is byte-identical.
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "main-content");
        await RunGitAsync(origin.Path, "checkout", "-b", "feature");
        await WriteAndCommitAsync(origin.Path, "feature.txt", "feature-content");

        await using var handle = await NewProvider().PrepareAsync(
            WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "feature", DefaultRef = "main" }), CancellationToken.None);

        File.Exists(Path.Combine(handle.Directory, "feature.txt")).ShouldBeTrue("the present soft ref is checked out — no fallback when it still exists");
    }

    [Fact]
    public async Task A_pruned_soft_ref_falls_back_to_the_default_branch()
    {
        // Correction-4: a session continue whose prior branch was DELETED (a merged PR auto-deletes it) must NOT fail —
        // it falls back to the default branch carried in DefaultRef. The continuing run still gets a workspace.
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "main-content");
        await RunGitAsync(origin.Path, "checkout", "-b", "feature");
        await WriteAndCommitAsync(origin.Path, "feature.txt", "feature-content");
        await RunGitAsync(origin.Path, "checkout", "main");
        await RunGitAsync(origin.Path, "branch", "-D", "feature");   // the prior branch is now pruned on the remote

        await using var handle = await NewProvider().PrepareAsync(
            WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "feature", DefaultRef = "main" }), CancellationToken.None);

        File.Exists(Path.Combine(handle.Directory, "README.md")).ShouldBeTrue("the run fell back to the default branch and still got a workspace");
        File.Exists(Path.Combine(handle.Directory, "feature.txt")).ShouldBeFalse("the default branch never carried the pruned branch's file — proof it cloned main, not feature");
    }

    [Fact]
    public async Task A_pruned_hard_ref_still_fails_loud()
    {
        // A HARD ref (DefaultRef null — no session fallback) that is gone must STILL fail the clone, byte-identical to
        // before: an explicit ref's intent is never silently rewritten to the default branch.
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "main-content");

        await Should.ThrowAsync<WorkspaceException>(async () =>
            await NewProvider().PrepareAsync(
                WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "ghost-branch" }), CancellationToken.None));
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
            WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Token = "secret-token", TokenUsername = "x-access-token" }), CancellationToken.None);

        var config = await File.ReadAllTextAsync(Path.Combine(handle.Directory, ".git", "config"));
        config.ShouldNotContain("secret-token", Case.Insensitive, "the token must be stripped from the persisted remote");
    }

    [Fact]
    public async Task Failed_clone_throws_a_workspace_exception()
    {
        if (!await GitAvailableAsync()) return;

        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));

        await Should.ThrowAsync<WorkspaceException>(async () =>
            await NewProvider().PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(missing) }), CancellationToken.None));
    }

    [Fact]
    public void Kind_is_local() => NewProvider().Kind.ShouldBe("local");

    // ─── Change capture ──────────────────────────────────────────────────────

    // ── S1: PinnedSha — the immutable-base substrate ─────────────────────────────────

    [Fact]
    public async Task A_pinned_sha_materializes_the_exact_historical_commit_not_the_tip()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "version-A");
        var pin = await GitStdoutAsync(origin.Path, "rev-parse", "HEAD");
        await WriteAndCommitAsync(origin.Path, "file.txt", "version-B");   // the tip moves on

        await using var handle = await NewProvider().PrepareAsync(
            WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), PinnedSha = pin }), CancellationToken.None);

        (await File.ReadAllTextAsync(Path.Combine(handle.Directory, "file.txt"))).Trim()
            .ShouldBe("version-A", "the pin wins over the tip — every participant sees the SAME immutable base");
    }

    [Fact]
    public async Task A_pinned_workspace_diffs_against_the_pin_not_the_tip()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "version-A");
        var pin = await GitStdoutAsync(origin.Path, "rev-parse", "HEAD");
        await WriteAndCommitAsync(origin.Path, "file.txt", "version-B");

        await using var handle = await NewProvider().PrepareAsync(
            WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), PinnedSha = pin }), CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(handle.Directory, "agent.txt"), "the agent's own work");
        var changes = await handle.CaptureChangesAsync(CancellationToken.None);

        changes.ChangedFiles.ShouldBe(new[] { "agent.txt" }, customMessage: "the diff base is the PIN — version-B's tip change never bleeds into the captured patch");
        changes.Patch.ShouldContain("the agent's own work");
        changes.Patch.ShouldNotContain("version-B", customMessage: "provenance: the capture describes the agent's work over the pinned base, not a tip the agent never saw");
    }

    [Fact]
    public async Task A_pin_matching_the_tip_keeps_the_clone_shallow()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "v1");
        var pin = await GitStdoutAsync(origin.Path, "rev-parse", "HEAD");   // the tip has NOT moved — the common launch

        await using var handle = await NewProvider().PrepareAsync(
            WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), PinnedSha = pin }), CancellationToken.None);

        (await GitStdoutAsync(handle.Directory, "rev-parse", "HEAD")).ShouldBe(pin);
        (await GitStdoutAsync(handle.Directory, "rev-parse", "--is-shallow-repository"))
            .ShouldBe("true", "pin == the fetched tip ⇒ the cheap rung wins — the common launch must not pay a full-history clone for its pin");
    }

    [Fact]
    public async Task A_pin_on_a_branch_the_clone_never_fetched_still_materializes()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "main-content");
        // The pin lives on a DIVERGENT branch (scan M1's shape: the clone's branch context and the pin disagree —
        // e.g. a reviewer cloning the default while the pin rides the operator's BaseBranch).
        await RunGitAsync(origin.Path, "checkout", "-b", "release/2.x");
        await WriteAndCommitAsync(origin.Path, "file.txt", "release-content");
        var pin = await GitStdoutAsync(origin.Path, "rev-parse", "HEAD");
        await RunGitAsync(origin.Path, "checkout", "main");

        await using var handle = await NewProvider().PrepareAsync(
            WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), Ref = "main", PinnedSha = pin }), CancellationToken.None);

        (await File.ReadAllTextAsync(Path.Combine(handle.Directory, "file.txt"))).Trim()
            .ShouldBe("release-content", "the all-branches rung rescues a cross-branch pin — the single-branch shallow clone alone could never reach it");
    }

    [Fact]
    public async Task A_missing_pinned_sha_fails_the_provision_loud_naming_the_pin()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "file.txt", "content");

        const string bogus = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";

        var ex = await Should.ThrowAsync<WorkspaceException>(() => NewProvider().PrepareAsync(
            WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path), PinnedSha = bogus }), CancellationToken.None));

        ex.Message.ShouldContain(bogus, customMessage: "the pin is a freshness guarantee — a stale/unpushed pin fails LOUD, never a silent tip fallback");
    }

    [Fact]
    public async Task Captures_edits_new_files_and_deletions_as_a_diff()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "keep.txt", "original");
        await WriteAndCommitAsync(origin.Path, "remove.txt", "to be deleted");

        await using var handle = await NewProvider().PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }), CancellationToken.None);

        // The "agent" edits a tracked file, adds a new one, and deletes another.
        await File.WriteAllTextAsync(Path.Combine(handle.Directory, "keep.txt"), "edited by agent");
        await File.WriteAllTextAsync(Path.Combine(handle.Directory, "new.txt"), "brand new");
        File.Delete(Path.Combine(handle.Directory, "remove.txt"));

        var changes = await handle.CaptureChangesAsync(CancellationToken.None);

        changes.IsEmpty.ShouldBeFalse();
        changes.ChangedFiles.ShouldBe(new[] { "keep.txt", "new.txt", "remove.txt" }, ignoreOrder: true);
        changes.Patch.ShouldContain("edited by agent");
        changes.Patch.ShouldContain("brand new");

        // The per-file diffstat is captured alongside (git ground truth, --numstat) — a pure-add file has 0 deletions,
        // a pure-delete 0 additions, so "+X −Y" is durable even once the patch is offloaded.
        changes.FileStats.Select(s => s.Path).ShouldBe(changes.ChangedFiles, ignoreOrder: true, "the diffstat covers exactly the changed files");

        var added = changes.FileStats.Single(s => s.Path == "new.txt");
        added.Additions.ShouldBe(1, "the brand-new single-line file added one line");
        added.Deletions.ShouldBe(0, "a brand-new file is a pure addition");

        var removed = changes.FileStats.Single(s => s.Path == "remove.txt");
        removed.Additions.ShouldBe(0, "a deleted file is a pure deletion");
        removed.Deletions.ShouldBe(1, "the deleted single-line file removed one line");
    }

    [Fact]
    public async Task Captures_nothing_when_the_agent_made_no_changes()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "unchanged");

        await using var handle = await NewProvider().PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }), CancellationToken.None);

        var changes = await handle.CaptureChangesAsync(CancellationToken.None);

        changes.IsEmpty.ShouldBeTrue();
        changes.ChangedFiles.ShouldBeEmpty();
        changes.Patch.ShouldBeEmpty();
        changes.FileStats.ShouldBeEmpty("no changes → no diffstat");
    }

    [Fact]
    public async Task Captures_a_renamed_files_diffstat_under_its_new_name_matching_the_changed_file()
    {
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "old.ts", "line1\nline2\nline3\n");

        await using var handle = await NewProvider().PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }), CancellationToken.None);

        // The "agent" renames the file and adds a line — git renders this in numstat as a folded "old => new" path,
        // but lists it by its NEW name in --name-only. The capture must resolve the stat to the new name so a consumer
        // can join FileStats↔ChangedFiles (robust whether git's rename detection is on or off in the host config).
        File.Move(Path.Combine(handle.Directory, "old.ts"), Path.Combine(handle.Directory, "new.ts"));
        await File.WriteAllTextAsync(Path.Combine(handle.Directory, "new.ts"), "line1\nline2\nline3\nline4\n");

        var changes = await handle.CaptureChangesAsync(CancellationToken.None);

        changes.FileStats.Select(s => s.Path).ShouldBe(changes.ChangedFiles, ignoreOrder: true, "every per-file stat keys on a real changed-file path — no orphaned rename key");
        changes.FileStats.ShouldNotContain(s => s.Path.Contains("=>"), "the rename resolved to its new name, not a raw 'old => new' path");
        changes.FileStats.ShouldContain(s => s.Path == "new.ts", "the renamed file's stat is keyed on its new name");
    }

    [Fact]
    public async Task Captures_committed_work_against_the_cloned_base()
    {
        // If the agent commits (HEAD moves), the diff is still taken vs the cloned base — committed work is captured.
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "base");

        await using var handle = await NewProvider().PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }), CancellationToken.None);

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

    // ─── Multi-repo workspace (multi-repo PR2) ────────────────────────────────

    [Fact]
    public async Task Single_repo_workspace_clones_flat_at_the_root_with_no_manifest()
    {
        // The locked invariant: a single-repo workspace clones flat into the root, cwd IS that clone, and there is
        // NO WORKSPACE.md — byte-identical to before. The one Repositories entry's directory equals Directory.
        if (!await GitAvailableAsync()) return;

        using var origin = new TempDir();
        await SeedOriginAsync(origin.Path, "README.md", "x");

        await using var handle = await NewProvider().PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = AsFileUrl(origin.Path) }), CancellationToken.None);

        handle.Repositories.Count.ShouldBe(1);
        handle.Repositories[0].Directory.ShouldBe(handle.Directory, "single-repo cwd IS the clone (not a subdir)");
        Directory.Exists(Path.Combine(handle.Directory, ".git")).ShouldBeTrue();
        File.Exists(Path.Combine(handle.Directory, "WORKSPACE.md")).ShouldBeFalse("a single-repo workspace is left pristine — no manifest");
    }

    [Fact]
    public async Task Clones_multiple_repos_under_a_shared_root_with_a_manifest()
    {
        if (!await GitAvailableAsync()) return;

        using var web = new TempDir();
        using var api = new TempDir();
        await SeedOriginAsync(web.Path, "web.txt", "web-content");
        await SeedOriginAsync(api.Path, "api.txt", "api-content");

        var provision = MultiRepo(
            (alias: "web", path: web.Path, access: WorkspaceAccess.Write, primary: true),
            (alias: "api", path: api.Path, access: WorkspaceAccess.Read, primary: false));

        string root;
        await using (var handle = await NewProvider().PrepareAsync(provision, CancellationToken.None))
        {
            root = handle.Directory;

            handle.Repositories.Count.ShouldBe(2);
            var webRepo = handle.Repositories.Single(r => r.Alias == "web");
            var apiRepo = handle.Repositories.Single(r => r.Alias == "api");

            // Auto cwd for a multi-repo workspace is the shared root; each repo is a sibling subdir under it.
            handle.Directory.ShouldBe(root);
            webRepo.Directory.ShouldBe(Path.Combine(root, "web"));
            apiRepo.Directory.ShouldBe(Path.Combine(root, "api"));
            webRepo.Access.ShouldBe(WorkspaceAccess.Write);
            apiRepo.Access.ShouldBe(WorkspaceAccess.Read);

            File.Exists(Path.Combine(webRepo.Directory, "web.txt")).ShouldBeTrue("the web repo is cloned into its subdir");
            File.Exists(Path.Combine(apiRepo.Directory, "api.txt")).ShouldBeTrue("the api repo is cloned into its subdir");

            var manifest = await File.ReadAllTextAsync(Path.Combine(root, "WORKSPACE.md"));
            manifest.ShouldContain("primary, writable", customMessage: "the manifest marks the primary writable repo");
            manifest.ShouldContain("read-only context", customMessage: "the manifest records the api repo's read-only access");
        }

        Directory.Exists(root).ShouldBeFalse("DisposeAsync removes the WHOLE multi-repo tree");
    }

    [Fact]
    public async Task Multi_repo_capture_returns_the_primary_repos_changes()
    {
        // Slice-2 scope: capture targets the PRIMARY repo. A secondary repo's edit is NOT in the captured diff yet
        // (per-repo result surfacing is the next slice). This pins the primary-capture boundary explicitly.
        if (!await GitAvailableAsync()) return;

        using var web = new TempDir();
        using var api = new TempDir();
        await SeedOriginAsync(web.Path, "web.txt", "web-base");
        await SeedOriginAsync(api.Path, "api.txt", "api-base");

        var provision = MultiRepo(
            (alias: "web", path: web.Path, access: WorkspaceAccess.Write, primary: true),
            (alias: "api", path: api.Path, access: WorkspaceAccess.Write, primary: false));

        await using var handle = await NewProvider().PrepareAsync(provision, CancellationToken.None);

        var webDir = handle.Repositories.Single(r => r.Alias == "web").Directory;
        var apiDir = handle.Repositories.Single(r => r.Alias == "api").Directory;
        await File.WriteAllTextAsync(Path.Combine(webDir, "web.txt"), "edited in primary");
        await File.WriteAllTextAsync(Path.Combine(apiDir, "api.txt"), "edited in secondary");

        var changes = await handle.CaptureChangesAsync(CancellationToken.None);

        changes.ChangedFiles.ShouldBe(new[] { "web.txt" }, "capture returns ONLY the primary repo's changes in this slice");
        changes.Patch.ShouldContain("edited in primary");
        changes.Patch.ShouldNotContain("edited in secondary", customMessage: "a secondary repo's edit is not in the primary capture");
    }

    [Fact]
    public async Task PrimaryRepo_cwd_mode_points_cwd_at_the_primary_subdir_in_a_multi_repo_workspace()
    {
        if (!await GitAvailableAsync()) return;

        using var web = new TempDir();
        using var api = new TempDir();
        await SeedOriginAsync(web.Path, "web.txt", "w");
        await SeedOriginAsync(api.Path, "api.txt", "a");

        var provision = MultiRepo(
            (alias: "web", path: web.Path, access: WorkspaceAccess.Write, primary: true),
            (alias: "api", path: api.Path, access: WorkspaceAccess.Read, primary: false)) with { CwdMode = WorkspaceCwdMode.PrimaryRepo };

        await using var handle = await NewProvider().PrepareAsync(provision, CancellationToken.None);

        handle.Directory.ShouldBe(handle.Repositories.Single(r => r.Alias == "web").Directory, "PrimaryRepo cwd mode runs the harness inside the primary repo even in a multi-repo workspace");
    }

    // ─── Multi-repo per-repo capture/push (multi-repo PR3) ────────────────────

    [Fact]
    public async Task PrimaryAlias_reports_the_primary_repos_alias()
    {
        if (!await GitAvailableAsync()) return;

        using var web = new TempDir();
        using var api = new TempDir();
        await SeedOriginAsync(web.Path, "web.txt", "w");
        await SeedOriginAsync(api.Path, "api.txt", "a");

        await using var handle = await NewProvider().PrepareAsync(MultiRepo(
            (alias: "web", path: web.Path, access: WorkspaceAccess.Write, primary: true),
            (alias: "api", path: api.Path, access: WorkspaceAccess.Read, primary: false)), CancellationToken.None);

        handle.PrimaryAlias.ShouldBe("web");
    }

    [Fact]
    public async Task Capture_by_alias_returns_each_repos_own_changes()
    {
        // The slice-3 surface: each writable repo is captured INDEPENDENTLY by alias — web's diff has only web's
        // edit, api's only api's. This is what the executor folds into RepositoryResults.
        if (!await GitAvailableAsync()) return;

        using var web = new TempDir();
        using var api = new TempDir();
        await SeedOriginAsync(web.Path, "web.txt", "web-base");
        await SeedOriginAsync(api.Path, "api.txt", "api-base");

        await using var handle = await NewProvider().PrepareAsync(MultiRepo(
            (alias: "web", path: web.Path, access: WorkspaceAccess.Write, primary: true),
            (alias: "api", path: api.Path, access: WorkspaceAccess.Write, primary: false)), CancellationToken.None);

        var webDir = handle.Repositories.Single(r => r.Alias == "web").Directory;
        var apiDir = handle.Repositories.Single(r => r.Alias == "api").Directory;
        await File.WriteAllTextAsync(Path.Combine(webDir, "web.txt"), "edited in web");
        await File.WriteAllTextAsync(Path.Combine(apiDir, "api.txt"), "edited in api");

        var webChanges = await handle.CaptureChangesAsync("web", CancellationToken.None);
        var apiChanges = await handle.CaptureChangesAsync("api", CancellationToken.None);

        webChanges.ChangedFiles.ShouldBe(new[] { "web.txt" });
        webChanges.Patch.ShouldContain("edited in web");
        webChanges.Patch.ShouldNotContain("edited in api", customMessage: "the web capture is scoped to the web repo");

        apiChanges.ChangedFiles.ShouldBe(new[] { "api.txt" });
        apiChanges.Patch.ShouldContain("edited in api");
    }

    [Fact]
    public async Task Capture_by_an_unknown_alias_throws()
    {
        if (!await GitAvailableAsync()) return;

        using var web = new TempDir();
        using var api = new TempDir();
        await SeedOriginAsync(web.Path, "web.txt", "w");
        await SeedOriginAsync(api.Path, "api.txt", "a");

        await using var handle = await NewProvider().PrepareAsync(MultiRepo(
            (alias: "web", path: web.Path, access: WorkspaceAccess.Write, primary: true),
            (alias: "api", path: api.Path, access: WorkspaceAccess.Write, primary: false)), CancellationToken.None);

        (await Should.ThrowAsync<WorkspaceException>(() => handle.CaptureChangesAsync("nope", CancellationToken.None)))
            .Message.ShouldContain("Unknown repository alias");
    }

    [Fact]
    public async Task Push_by_a_read_only_alias_is_refused_before_any_git()
    {
        // A read-only context repo must never be pushed — the alias-scoped push fails loud BEFORE touching git.
        if (!await GitAvailableAsync()) return;

        using var web = new TempDir();
        using var api = new TempDir();
        await SeedOriginAsync(web.Path, "web.txt", "w");
        await SeedOriginAsync(api.Path, "api.txt", "a");

        await using var handle = await NewProvider().PrepareAsync(MultiRepo(
            (alias: "web", path: web.Path, access: WorkspaceAccess.Write, primary: true),
            (alias: "api", path: api.Path, access: WorkspaceAccess.Read, primary: false)), CancellationToken.None);

        (await Should.ThrowAsync<WorkspaceException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync("api", "codespace/run-x", CancellationToken.None)))
            .Message.ShouldContain("read-only context");
    }

    /// <summary>Build a multi-repo provision from (alias, local-origin-path, access, primary) tuples, with Auto cwd.</summary>
    private static WorkspaceProvisionRequest MultiRepo(params (string alias, string path, WorkspaceAccess access, bool primary)[] repos) => new()
    {
        PrimaryAlias = repos.FirstOrDefault(r => r.primary).alias,
        Repositories = repos.Select(r => new WorkspaceRepositoryProvision
        {
            Alias = r.alias,
            CloneRequest = new WorkspaceRequest { RepositoryUrl = AsFileUrl(r.path) },
            Access = r.access,
            IsPrimary = r.primary,
        }).ToList(),
    };

    // ─── Multi-repo layout safety: traversal / collision guard (fold of the slice-2 review) ───

    [Theory]
    [InlineData("web", true)]
    [InlineData("api-service", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("../escape", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("/etc/cron.d", false)]
    public void IsSafeMountSegment_accepts_only_a_single_unrooted_directory_name(string segment, bool expectedSafe) =>
        LocalGitWorkspaceProvider.IsSafeMountSegment(segment).ShouldBe(expectedSafe);

    [Fact]
    public async Task A_multi_repo_provision_with_a_traversing_mount_path_is_refused_before_any_clone()
    {
        // A repo whose mount path escapes the workspace root (.. or absolute) must fail LOUD at provision time,
        // never clone outside the per-run GUID dir (which would escape isolation + survive DisposeAsync).
        var provision = new WorkspaceProvisionRequest
        {
            PrimaryAlias = "web",
            Repositories = new[]
            {
                new WorkspaceRepositoryProvision { Alias = "web", CloneRequest = new WorkspaceRequest { RepositoryUrl = "file:///x" }, IsPrimary = true },
                new WorkspaceRepositoryProvision { Alias = "evil", Path = "../escape", CloneRequest = new WorkspaceRequest { RepositoryUrl = "file:///y" } },
            },
        };

        (await Should.ThrowAsync<WorkspaceException>(() => NewProvider().PrepareAsync(provision, CancellationToken.None)))
            .Message.ShouldContain("Unsafe repository mount path");
    }

    [Fact]
    public async Task A_multi_repo_provision_with_a_duplicate_alias_is_refused()
    {
        var provision = MultiRepo(
            (alias: "api", path: "/tmp/whatever", access: WorkspaceAccess.Write, primary: true),
            (alias: "api", path: "/tmp/whatever2", access: WorkspaceAccess.Write, primary: false));

        (await Should.ThrowAsync<WorkspaceException>(() => NewProvider().PrepareAsync(provision, CancellationToken.None)))
            .Message.ShouldContain("Duplicate repository alias");
    }

    [Fact]
    public async Task Clones_a_repo_into_an_explicit_mount_path_distinct_from_its_alias()
    {
        if (!await GitAvailableAsync()) return;

        using var web = new TempDir();
        using var lib = new TempDir();
        await SeedOriginAsync(web.Path, "web.txt", "w");
        await SeedOriginAsync(lib.Path, "lib.txt", "l");

        var provision = new WorkspaceProvisionRequest
        {
            PrimaryAlias = "web",
            Repositories = new[]
            {
                new WorkspaceRepositoryProvision { Alias = "web", CloneRequest = new WorkspaceRequest { RepositoryUrl = AsFileUrl(web.Path) }, IsPrimary = true },
                // An explicit single-segment mount path that differs from the alias (the Path-wins-over-alias branch).
                new WorkspaceRepositoryProvision { Alias = "lib", Path = "shared", CloneRequest = new WorkspaceRequest { RepositoryUrl = AsFileUrl(lib.Path) }, Access = WorkspaceAccess.Read },
            },
        };

        await using var handle = await NewProvider().PrepareAsync(provision, CancellationToken.None);

        var libRepo = handle.Repositories.Single(r => r.Alias == "lib");
        libRepo.Directory.ShouldBe(Path.Combine(handle.Directory, "shared"), "the repo clones into its explicit mount Path, not its alias");
        File.Exists(Path.Combine(libRepo.Directory, "lib.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task Multi_repo_clone_strips_each_repos_token_from_its_own_git_config()
    {
        // The per-repo secret-hygiene invariant: a token on ANY repo is stripped from THAT repo's .git/config.
        if (!await GitAvailableAsync()) return;

        using var web = new TempDir();
        using var api = new TempDir();
        await SeedOriginAsync(web.Path, "web.txt", "w");
        await SeedOriginAsync(api.Path, "api.txt", "a");

        var provision = new WorkspaceProvisionRequest
        {
            PrimaryAlias = "web",
            Repositories = new[]
            {
                new WorkspaceRepositoryProvision { Alias = "web", CloneRequest = new WorkspaceRequest { RepositoryUrl = AsFileUrl(web.Path), Token = "web-secret-token", TokenUsername = "x-access-token" }, IsPrimary = true },
                new WorkspaceRepositoryProvision { Alias = "api", CloneRequest = new WorkspaceRequest { RepositoryUrl = AsFileUrl(api.Path), Token = "api-secret-token", TokenUsername = "x-access-token" }, Access = WorkspaceAccess.Write },
            },
        };

        await using var handle = await NewProvider().PrepareAsync(provision, CancellationToken.None);

        foreach (var repo in handle.Repositories)
        {
            var config = await File.ReadAllTextAsync(Path.Combine(repo.Directory, ".git", "config"));
            config.ShouldNotContain("secret-token", Case.Insensitive, $"the {repo.Alias} repo's token must be stripped from its persisted remote");
        }
    }

    [Fact]
    public async Task A_later_repos_clone_failure_removes_the_whole_partial_workspace()
    {
        // repo[0] clones fine, repo[1]'s origin is missing → the whole workspace tree (incl. the succeeded repo[0])
        // is removed by the catch, leaking nothing.
        if (!await GitAvailableAsync()) return;

        using var web = new TempDir();
        await SeedOriginAsync(web.Path, "web.txt", "w");
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));

        var provision = MultiRepo(
            (alias: "web", path: web.Path, access: WorkspaceAccess.Write, primary: true),
            (alias: "api", path: missing, access: WorkspaceAccess.Write, primary: false));

        var before = Directory.Exists(LocalGitWorkspaceProvider.WorkspacesRoot)
            ? Directory.GetDirectories(LocalGitWorkspaceProvider.WorkspacesRoot).Length : 0;

        await Should.ThrowAsync<WorkspaceException>(() => NewProvider().PrepareAsync(provision, CancellationToken.None));

        var after = Directory.Exists(LocalGitWorkspaceProvider.WorkspacesRoot)
            ? Directory.GetDirectories(LocalGitWorkspaceProvider.WorkspacesRoot).Length : 0;
        after.ShouldBe(before, "a partial multi-repo clone leaves no workspace dir behind");
    }

    // ─── Workspace janitor (reclaim clones orphaned by a crashed worker) ──────

    [Fact]
    public void StaleThreshold_env_var_name_is_pinned() =>
        // Renaming this breaks every operator who pinned a custom staleness window via env (Rule 8).
        LocalGitWorkspaceProvider.StaleThresholdEnvVar.ShouldBe("CODESPACE_AGENT_WORKSPACE_STALE_THRESHOLD");

    [Theory]
    [InlineData(3, true)]    // last write 3h ago, threshold 2h → stale
    [InlineData(1, false)]   // last write 1h ago → still within the window (a live run)
    public void IsStale_compares_age_against_threshold(int ageHours, bool expectedStale)
    {
        var now = new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc);

        LocalGitWorkspaceProvider.IsStale(now.AddHours(-ageHours), now, TimeSpan.FromHours(2)).ShouldBe(expectedStale);
    }

    [Fact]
    public void IsStale_is_strict_at_the_boundary() =>
        // Exactly == threshold is NOT stale — age must strictly EXCEED, so a run right at the limit is spared.
        LocalGitWorkspaceProvider.IsStale(
            new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromHours(2)).ShouldBeFalse();

    [Fact]
    public void ReadStaleThreshold_overrides_from_env_and_falls_back_on_absent_or_garbage()
    {
        var original = Environment.GetEnvironmentVariable(LocalGitWorkspaceProvider.StaleThresholdEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LocalGitWorkspaceProvider.StaleThresholdEnvVar, null);
            LocalGitWorkspaceProvider.ReadStaleThreshold().ShouldBe(TimeSpan.FromHours(2), "absent → the 2h default");

            Environment.SetEnvironmentVariable(LocalGitWorkspaceProvider.StaleThresholdEnvVar, "not-a-timespan");
            LocalGitWorkspaceProvider.ReadStaleThreshold().ShouldBe(TimeSpan.FromHours(2), "garbage → the default");

            Environment.SetEnvironmentVariable(LocalGitWorkspaceProvider.StaleThresholdEnvVar, "00:00:00");
            LocalGitWorkspaceProvider.ReadStaleThreshold().ShouldBe(TimeSpan.FromHours(2), "non-positive → the default (never sweep everything)");

            Environment.SetEnvironmentVariable(LocalGitWorkspaceProvider.StaleThresholdEnvVar, "00:30:00");
            LocalGitWorkspaceProvider.ReadStaleThreshold().ShouldBe(TimeSpan.FromMinutes(30), "a valid positive TimeSpan overrides the default");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalGitWorkspaceProvider.StaleThresholdEnvVar, original);
        }
    }

    [Fact]
    public void Sweep_reclaims_stale_workspaces_and_spares_fresh_ones()
    {
        using var root = new TempDir();   // an ISOLATED root, never the real WorkspacesRoot
        var now = new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc);

        var stale = Directory.CreateDirectory(Path.Combine(root.Path, "stale")).FullName;
        var fresh = Directory.CreateDirectory(Path.Combine(root.Path, "fresh")).FullName;
        Directory.SetLastWriteTimeUtc(stale, now.AddHours(-3));      // older than the 2h threshold
        Directory.SetLastWriteTimeUtc(fresh, now.AddMinutes(-30));   // within the window

        var reclaimed = NewProvider().SweepStale(root.Path, TimeSpan.FromHours(2), now, CancellationToken.None);

        reclaimed.ShouldBe(1);
        Directory.Exists(stale).ShouldBeFalse("a workspace untouched past the threshold is reclaimed");
        Directory.Exists(fresh).ShouldBeTrue("a workspace within the window (a live run) is never touched");
    }

    [Fact]
    public void Sweep_is_a_noop_when_the_storage_root_was_never_created()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cs-never-" + Guid.NewGuid().ToString("N"));

        NewProvider().SweepStale(missing, TimeSpan.FromHours(2), DateTime.UtcNow, CancellationToken.None).ShouldBe(0);
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

    private static async Task<string> GitStdoutAsync(string workdir, params string[] args)
    {
        var result = await new LocalProcessRunner().RunAsync(
            new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

        if (result.Status != SandboxStatus.Success)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

        return result.Stdout.Trim();
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
