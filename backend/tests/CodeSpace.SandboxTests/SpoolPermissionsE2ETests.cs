using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox E2E (high fidelity, Rule 12): the per-run spool is owner-only on the real kernel, under real bubblewrap,
/// and a confined run still works inside it. The spool holds <c>out.log</c>/<c>err.log</c> — the agent's output before
/// redaction — and sat at the worker's umask (a 0755 directory around a 0644 log) beside the 0700 config home. It is
/// now created 0700 at launch and its logs 0600. What has to stay true under that: bubblewrap still binds the config
/// home out of the 0700 spool, the agent still writes there, and the worker still reads the run back.
///
/// <para>The root lane runs the arm here; the non-root lane runs the same arm as the shipped worker's uid through
/// <see cref="NonRootWorkerE2ETests"/>. Each arm that ran prints <see cref="RanMarker"/>, which the lanes require.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class SpoolPermissionsE2ETests(ITestOutputHelper output)
{
    /// <summary>Printed by every arm that actually ran.</summary>
    public const string RanMarker = "[spool-perms-e2e] ran";

    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Fact]
    public async Task A_confined_run_spools_owner_only_and_still_uses_its_config_home()
    {
        if (!OperatingSystem.IsLinux() || BubblewrapSandbox.Available is null && !BubblewrapSandbox.IsRequired) return;

        await OwnerOnlySpoolAsync("root");
    }

    /// <summary>The arm, for either lane: a confined durable run that writes to stdout, stderr and its config home.</summary>
    internal async Task OwnerOnlySpoolAsync(string lane)
    {
        if (!OperatingSystem.IsLinux()) return;   // bubblewrap is Linux-only (Rule 12.1); each caller already gates on its lane's posture

        BubblewrapSandbox.Available.ShouldNotBeNull($"this arm asserts the spool under REAL confinement ({BubblewrapSandbox.UnavailableReason})");

        var workspace = Directory.CreateTempSubdirectory("cs-spool-perms-").FullName;
        var key = Guid.NewGuid().ToString("N");
        var runner = new LocalProcessRunner();

        try
        {
            var spec = new SandboxSpec
            {
                Command = "/bin/sh", Args = ["-c", "printf 'raw out\\n'; printf 'raw err\\n' >&2; touch \"$CS_HOME/probe\" && printf 'home-ok\\n'"],
                WorkingDirectory = workspace, TimeoutSeconds = 60, ConfigHomeEnvVars = ["CS_HOME"],
            };

            var handle = await runner.LaunchAsync(spec, key, CancellationToken.None);
            var lines = new List<string>();
            var result = await runner.AttachAsync(handle, (frame, _) => { lines.Add(frame.Text.Trim()); return Task.CompletedTask; }, CancellationToken.None);

            handle.Confinement.ShouldNotBeNull("fixture check: the run was confined by bubblewrap, or this proves nothing about binding out of a 0700 spool");
            result.Status.ShouldBe(SandboxStatus.Success, $"the confined run must still complete: stderr '{result.Stderr}'");
            lines.ShouldContain("raw out", "the worker still reads the run's stdout back out of its owner-only spool");
            lines.ShouldContain("home-ok", "bubblewrap still binds the config home out of the 0700 spool, and the agent writes there");
            File.Exists(Path.Combine(LocalProcessRunner.ConfigHomePath(handle.SpoolDirectory), "probe")).ShouldBeTrue("the agent's config-home write landed in the bound directory");

            File.GetUnixFileMode(handle.SpoolDirectory).ShouldBe(OwnerOnlyDirectory, $"the spool {handle.SpoolDirectory} must be owner-only — check `stat -c %a` on it");
            File.GetUnixFileMode(Path.Combine(handle.SpoolDirectory, "out.log")).ShouldBe(OwnerOnlyFile, "out.log is the agent's raw stdout");
            File.GetUnixFileMode(Path.Combine(handle.SpoolDirectory, "err.log")).ShouldBe(OwnerOnlyFile, "err.log is the agent's raw stderr");
            File.GetUnixFileMode(LocalProcessRunner.ConfigHomePath(handle.SpoolDirectory)).ShouldBe(OwnerOnlyDirectory, "the config home stays owner-only");

            output.WriteLine($"{RanMarker} {(lane == "root" ? "" : lane + " ")}uid={NonRootWorker.EffectiveUid()} confined=True");
        }
        finally
        {
            try { Directory.Delete(LocalProcessRunner.SpoolDirectoryFor(key), recursive: true); } catch { /* best-effort */ }
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }
}
