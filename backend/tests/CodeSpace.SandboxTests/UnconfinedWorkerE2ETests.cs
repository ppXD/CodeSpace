using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12), the UNCONFINED lane: the worker image as docker-compose.yml ships
/// it — uid 1654 with no capabilities, <c>ip</c> and <c>nft</c> installed, nothing letting bubblewrap confine, and
/// <c>Sandbox:RequireConfinement</c> off. A confining worker that cannot filter an allowlist severs it; here nothing
/// would enforce that, so severing would mean the worker's network. The REAL runner plans the run into its namespace
/// on the binaries alone, as it always has, so the setup's refusal aborts the launch — durable or not — and the
/// admission predicts that same namespace.
///
/// <para>Unconfined, Codex's own sandbox is the only boundary its commands meet, so this lane also runs the
/// repository-config arm that needs exactly that posture: a multi-repo Codex run whose agent must not be able to write
/// any repository's <c>.git</c> or <c>.codex</c> (<see cref="RepositoryConfigE2ETests.CodexKeepsEveryRepositorysMetadataReadOnlyAsync"/>).</para>
///
/// <para>Selected by its trait alone (<c>--filter Category=SandboxUnconfined</c>); the lane runs it as uid 1654 with
/// <c>CODESPACE_BWRAP_PATH</c> naming no binary and <c>Sandbox__RequireConfinement</c> unset. Each arm asserts that
/// posture first (<see cref="RequirePosture"/>) and prints <see cref="RanMarker"/>, or for the Codex arm
/// <see cref="RepositoryConfigE2ETests.RanMarker"/>, which the lane requires.</para>
/// </summary>
[Trait("Category", Category)]
public sealed class UnconfinedWorkerE2ETests(ITestOutputHelper output)
{
    /// <summary>The trait value the unconfined lane filters on.</summary>
    public const string Category = "SandboxUnconfined";

    /// <summary>Printed by every arm that ran.</summary>
    public const string RanMarker = "[unconfined-e2e] ran";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_allowlist_run_this_worker_cannot_filter_is_never_launched_on_the_worker_s_network(bool durable)
    {
        if (!RequirePosture()) return;

        var key = Guid.NewGuid().ToString("N");
        var spec = new SandboxSpec { Command = "/bin/true", AllowNetwork = true, EgressAllowlist = ["1.1.1.1"], TimeoutSeconds = 30 };
        var runner = new LocalProcessRunner();

        Task Launch() => durable ? runner.LaunchAsync(spec, key, CancellationToken.None) : runner.RunAsync(spec, CancellationToken.None);

        try
        {
            var refused = await Should.ThrowAsync<InvalidOperationException>(Launch, customMessage: "severed here, the run would have shared the worker's network: nothing on this host enforces a severance");

            refused.Message.ShouldContain("Filtered-egress netns setup failed", customMessage: $"planned into its namespace on the binaries alone, the launch aborts where the setup is refused; check `ip netns add probe` as uid {NonRootWorker.EffectiveUid()}");
        }
        finally
        {
            try { Directory.Delete(LocalProcessRunner.SpoolDirectoryFor(key), recursive: true); } catch { /* best-effort */ }
        }

        (await NetnsExistsAsync(FilteredEgressPlan.NamespaceFor(key))).ShouldBeFalse("and no namespace was left behind for it");

        output.WriteLine($"{RanMarker} allowlist-never-unfiltered {(durable ? "durable" : "one-shot")} uid={NonRootWorker.EffectiveUid()} filter-unavailable=({FilteredEgressNetns.FilterUnavailableReason})");
    }

    [Fact]
    public void The_admission_predicts_the_namespace_the_launch_plans()
    {
        if (!RequirePosture()) return;

        // Brokered, with no socket: only a child with a network of its own needs one, and here the launch plans this run
        // into a namespace — so the admission must refuse it, from the same host fact, before anything is spent.
        var spec = new SandboxSpec { Command = "agent", AllowNetwork = true, EgressAllowlist = ["api.anthropic.com"], ModelBrokerPort = 43121 };

        var refused = Should.Throw<SealedEgressUnavailableException>(() => new LocalProcessRunner().EnsureEgressAdmissible(spec), "the admission must read the host fact the launch reads, which plans this run into its namespace here");

        refused.Cause.ShouldBe(SealedEgressUnavailableException.CauseBrokerSocketUnavailable);

        output.WriteLine($"{RanMarker} admission uid={NonRootWorker.EffectiveUid()}");
    }

    [Fact]
    public async Task A_multi_repo_codex_run_cannot_write_a_repositorys_git_metadata_where_its_own_sandbox_is_the_boundary()
    {
        if (!RequirePosture()) return;

        using var arms = new RepositoryConfigE2ETests(output);
        await arms.CodexKeepsEveryRepositorysMetadataReadOnlyAsync("unconfined");
    }

    /// <summary>
    /// True on Linux once the posture is proved; false on any other OS (Rule 12.1). On Linux a missing piece FAILS the
    /// test: an arm that ran where bubblewrap confines, or as root, proves nothing about the posture it is named for.
    /// </summary>
    private static bool RequirePosture()
    {
        if (!OperatingSystem.IsLinux()) return false;

        NonRootWorker.EffectiveUid().ShouldNotBe(0u, "this is the unconfined lane: run it as the worker's uid with no capabilities (setpriv --reuid 1654 --regid 1654 --clear-groups --inh-caps=-all --bounding-set=-all --no-new-privs)");
        BubblewrapSandbox.Available.ShouldBeNull($"nothing may confine in this lane: point {BubblewrapSandbox.CommandEnvVar} at no binary, as a runtime that denies bubblewrap leaves the worker");
        BubblewrapSandbox.IsRequired.ShouldBeFalse("Sandbox:RequireConfinement refuses every run on an unconfined host; the lane leaves it unset, as docker-compose.yml ships the worker");
        FilteredEgressNetns.IsSupported.ShouldBeTrue("ip and nft must be installed, as the image ships them: the binaries that plan an allowlist run into its namespace here");
        FilteredEgressNetns.CanFilter.ShouldBeFalse("and this uid must not be able to filter one, the posture where a severed allowlist would have meant the worker's network");

        return true;
    }

    /// <summary>True when <paramref name="ns"/> is still a live network namespace (parsing <c>ip netns list</c>).</summary>
    private static async Task<bool> NetnsExistsAsync(string ns)
    {
        var psi = new ProcessStartInfo { FileName = "ip", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("netns");
        psi.ArgumentList.Add("list");

        using var p = Process.Start(psi)!;
        var listing = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();

        return listing.Split('\n').Any(line => line.Trim().Split(' ').FirstOrDefault() == ns);
    }
}
