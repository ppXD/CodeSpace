using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the local runner's refusal of a brokered run whose child would have a network of its own on this host but no
/// way to its broker — the admission the executor asks for before anything is spent. The executor asking at all, and
/// landing the refusal typed with no spend and no process, is pinned one tier up in <c>AgentRunExecutorTests</c>; the
/// worker that cannot build a namespace admitting such a run is pinned on the real kernel in the non-root lane.
///
/// <para>The helper is ASKED whether it runs the relay (<see cref="ModelBrokerRelay.HelperRunsRelay"/>), so the stand-ins
/// here are real executables: one answers the way the relay does, one the way the MCP proxy from before the relay
/// does. The shipped helper's own answer is pinned below against the real apphost beside this assembly. POSIX only.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class SealedEgressAdmissionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cs-relay-helper-").FullName;

    [Theory]
    [InlineData(true, true, Helper.Relay, true, null)]                                                              // everything the relay needs: admitted
    [InlineData(true, false, Helper.Relay, true, SealedEgressUnavailableException.CauseBrokerSocketUnavailable)]  // the broker could not bind the socket the relay connects to
    [InlineData(true, true, Helper.Missing, true, SealedEgressUnavailableException.CauseRelayMissing)]            // no helper to run the relay
    [InlineData(true, true, Helper.PreRelay, true, SealedEgressUnavailableException.CauseRelayMissing)]           // a helper built before the relay: its MCP proxy reads any argv as its own
    [InlineData(true, true, Helper.SelfContained, true, SealedEgressUnavailableException.CauseRelayMissing)]      // a self-contained publish, which cannot start from the files the sandbox binds
    [InlineData(true, true, Helper.NotExecutable, true, SealedEgressUnavailableException.CauseRelayMissing)]      // a file that cannot start at all
    [InlineData(true, true, Helper.BindsAnyway, true, SealedEgressUnavailableException.CauseRelayMissing)]        // one that starts its CLI on a port it cannot have bound is not the relay
    [InlineData(true, false, Helper.Missing, true, SealedEgressUnavailableException.CauseBrokerSocketUnavailable)] // both missing: the socket is named first, since it is per run
    [InlineData(true, false, Helper.Missing, false, null)]                                                         // the child shares the worker's network: it calls loopback itself
    [InlineData(false, false, Helper.Missing, true, null)]                                                         // nothing brokered: nothing to reach
    public void Each_wall_a_private_network_child_can_hit_is_named(bool port, bool socket, Helper helper, bool privateNetwork, string? expected)
    {
        if (OperatingSystem.IsWindows()) return;

        var spec = new SandboxSpec { Command = "agent", ModelBrokerPort = port ? 43121 : null, ModelBrokerSocketPath = socket ? "/spool/k/broker/seg/s" : null };

        var refusal = LocalProcessRunner.RelayRefusal(spec, privateNetwork, HelperAt(helper));

        if (expected is null) refusal.ShouldBeNull();
        else refusal.ShouldNotBeNull().ShouldStartWith(expected, customMessage: "the cause leads with the wall");
    }

    [Fact]
    public void A_missing_helper_is_named_by_the_path_that_was_looked_for()
    {
        var spec = new SandboxSpec { Command = "agent", ModelBrokerPort = 43121, ModelBrokerSocketPath = "/spool/k/broker/seg/s" };

        LocalProcessRunner.RelayRefusal(spec, childNetworkIsPrivate: true, "/nowhere/codespace-mcp").ShouldBe($"{SealedEgressUnavailableException.CauseRelayMissing} (looked for /nowhere/codespace-mcp)", "an operator is told where the worker looked, which is where CODESPACE_MCP_PROXY_PATH points or next to its assembly");
    }

    [Theory]
    [InlineData(Helper.PreRelay, "{path} did not answer as the relay: it predates it, or cannot start")]
    [InlineData(Helper.SelfContained, "{path} is a self-contained publish of several files, which cannot start from the files the sandbox binds")]
    public void A_helper_that_is_there_but_cannot_relay_is_named_with_why(Helper helper, string why)
    {
        if (OperatingSystem.IsWindows()) return;

        var spec = new SandboxSpec { Command = "agent", ModelBrokerPort = 43121, ModelBrokerSocketPath = "/spool/k/broker/seg/s" };
        var path = HelperAt(helper);

        LocalProcessRunner.RelayRefusal(spec, childNetworkIsPrivate: true, path).ShouldBe($"{SealedEgressUnavailableException.CauseRelayMissing} ({why.Replace("{path}", path)})", "the operator is told which file and what is wrong with it, since the file is there");
    }

    [Fact]
    public void The_shipped_helper_answers_as_the_relay()
    {
        if (OperatingSystem.IsWindows()) return;

        // The real apphost the unit build lands beside this assembly, where LocalProcessRunner.McpProxyBinaryPath() looks.
        var shipped = Path.Combine(AppContext.BaseDirectory, "codespace-mcp");
        File.Exists(shipped).ShouldBeTrue($"fixture: the unit build lands the codespace-mcp apphost at {shipped}");

        ModelBrokerRelay.HelperRunsRelay(shipped).ShouldBeTrue("the helper this release ships must answer the admission's question as the relay — refused on a port it cannot bind, having started nothing — or every brokered run whose network is its own is refused");
    }

    [Fact]
    public void Only_a_yes_is_remembered_and_only_for_the_file_that_gave_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_dir, "replaced-codespace-mcp");

        WriteExecutable(path, PreRelayScript);
        ModelBrokerRelay.HelperRunsRelay(path).ShouldBeFalse("control: a helper from before the relay does not answer as it");

        WriteExecutable(path, RelayScript + "\n# replaced by this release's build");
        ModelBrokerRelay.HelperRunsRelay(path).ShouldBeTrue("a no is not remembered: the operator who replaces the helper is admitted on the next run, without a restart");

        WriteExecutable(path, PreRelayScript + "\n# rolled back to a build from before the relay");
        ModelBrokerRelay.HelperRunsRelay(path).ShouldBeFalse("a yes belongs to the file that gave it: a replaced helper is asked again");
    }

    [Theory]
    [InlineData(false, null, false, true, true)]                    // network off: severed wherever bwrap confines
    [InlineData(false, null, false, false, false)]                  // but unconfined, a network-off child shares the worker's network
    [InlineData(true, null, false, true, false)]                    // network on, no allowlist: the worker's network
    [InlineData(true, "api.anthropic.com", false, true, true)]      // an allowlist bwrap cannot enforce: severed
    [InlineData(true, "api.anthropic.com", true, false, true)]      // an allowlist in its filtered namespace, confined or not
    public void The_admission_reads_the_same_private_network_rule_as_the_launch(bool allowNetwork, string? allowlistHost, bool inNamespace, bool confines, bool expected)
    {
        var spec = new SandboxSpec { Command = "agent", AllowNetwork = allowNetwork, EgressAllowlist = allowlistHost is null ? null : [allowlistHost] };

        LocalProcessRunner.ChildNetworkIsPrivate(spec, inNamespace, confines).ShouldBe(expected);
    }

    [Fact]
    public void A_spec_with_no_broker_port_is_admitted_on_any_host() =>
        // Nothing to reach: every unbrokered run launches exactly as it always did.
        Should.NotThrow(() => new LocalProcessRunner().EnsureEgressAdmissible(new SandboxSpec { Command = "agent" }));

    [Fact]
    public void A_brokered_network_off_spec_without_its_socket_is_refused_exactly_where_this_host_would_sever_it()
    {
        // Honest on either host: unconfined hosts (macOS dev, a pod without userns) admit it untouched, because nothing
        // there gives the child a network of its own; a confining host refuses it, because nothing could carry it to its broker.
        var spec = new SandboxSpec { Command = "agent", ModelBrokerPort = 43121 };

        var thrown = Record.Exception(() => new LocalProcessRunner().EnsureEgressAdmissible(spec));

        if (BubblewrapSandbox.Available is null) thrown.ShouldBeNull();
        else thrown.ShouldBeOfType<SealedEgressUnavailableException>().Cause.ShouldBe(SealedEgressUnavailableException.CauseBrokerSocketUnavailable);
    }

    [Fact]
    public void The_refusal_is_an_unavailable_failure_that_names_its_cause_and_its_remedy()
    {
        IFailure failure = new SealedEgressUnavailableException(SealedEgressUnavailableException.CauseRelayMissing);

        failure.Kind.ShouldBe(FailureKind.Unavailable, "nothing about the launch can change to make it work — only the host can");
        failure.Code.ShouldBe(FailureCodes.SandboxSealedEgressUnavailable, "the wire name the supervisor and stored results key on is kept");
        failure.ClientMessage.ShouldBe("This host cannot give this run's sandbox a route to its model.");
        ((Exception)failure).Message.ShouldContain(SealedEgressUnavailableException.CauseRelayMissing, customMessage: "the operator must be told which wall it was");
        ((Exception)failure).Message.ShouldContain("CODESPACE_MCP_PROXY_PATH", customMessage: "and where to put the helper");
        ((Exception)failure).Message.ShouldContain("this release", customMessage: "and that an override must name a helper that has the relay");
        ((Exception)failure).Message.ShouldNotContain("CAP_NET_ADMIN", customMessage: "a namespaced run needs no privilege of the worker's any more; the remedy must not send an operator to grant one");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>The helper each row stands the worker's up with.</summary>
    public enum Helper { Relay, Missing, PreRelay, SelfContained, NotExecutable, BindsAnyway }

    /// <summary>Answers the admission's question the way the relay does: asked to listen on a port something already holds, it exits with the relay's listen-failed status and starts nothing.</summary>
    private static readonly string RelayScript = $"#!/bin/sh\necho 'codespace-mcp relay: cannot listen on 127.0.0.1:1 (AddressAlreadyInUse); the CLI was not started.' >&2\nexit {ModelBrokerRelay.ListenFailedExitCode}";

    /// <summary>Answers the way a codespace-mcp from before the relay does: its MCP proxy reads any argv as its own and exits with its usage error.</summary>
    private const string PreRelayScript = "#!/bin/sh\necho 'The MCP proxy requires a socket path in CODESPACE_MCP_SOCKET.' >&2\nexit 2";

    private string HelperAt(Helper helper)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_dir, helper.ToString())).FullName;
        var path = Path.Combine(directory, "codespace-mcp");

        switch (helper)
        {
            case Helper.Relay: WriteExecutable(path, RelayScript); break;
            case Helper.PreRelay: WriteExecutable(path, PreRelayScript); break;
            case Helper.BindsAnyway: WriteExecutable(path, "#!/bin/sh\nexit 0"); break;
            case Helper.NotExecutable: File.WriteAllText(path, "not a program"); break;
            case Helper.SelfContained:
                WriteExecutable(path, RelayScript);
                File.WriteAllText(Path.Combine(directory, "codespace-mcp.runtimeconfig.json"), """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.8"}]}}""");
                break;
        }

        return path;
    }

    private static void WriteExecutable(string path, string script)
    {
        File.WriteAllText(path, script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
