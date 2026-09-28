using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the rootless worker posture: the committed seccomp profile (<c>backend/deploy/seccomp/codespace-worker.json</c>)
/// that lets the non-root, capability-less worker build bubblewrap's sandbox, and the boot line that tells an operator
/// which parts of that posture hold. That the profile really confines on a kernel is shown by running the worker image
/// under it as uid 1654; this tier pins what the file grants, so a narrowing or a widening is a reviewed diff rather
/// than a surprise on the next deploy.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RootlessWorkerPostureTests
{
    /// <summary>
    /// Every call moby v20.10.20's default allows only to a container holding a capability (the ptrace family, module
    /// loading, the new mount API, <c>open_by_handle_at</c>, <c>bpf</c>, <c>perf_event_open</c> and the rest), less the
    /// seven bubblewrap needs. The worker holds no capability, so none of these may be allowed. <c>ptrace</c> itself is
    /// not here: moby allows it to every container on kernel 4.8+.
    /// </summary>
    private static readonly string[] CapabilityGatedSyscalls =
    {
        "acct", "bpf", "chroot", "clock_settime", "delete_module", "fanotify_init", "finit_module", "fsconfig", "fsmount", "fsopen", "fspick", "get_mempolicy", "init_module",
        "ioperm", "iopl", "kcmp", "lookup_dcookie", "mbind", "mount_setattr", "move_mount", "name_to_handle_at", "open_by_handle_at", "open_tree", "perf_event_open",
        "pidfd_getfd", "process_madvise", "process_vm_readv", "process_vm_writev", "quotactl", "quotactl_fd", "reboot", "set_mempolicy", "setdomainname", "sethostname",
        "settimeofday", "stime", "syslog", "umount", "vhangup",
    };

    /// <summary>The fields of the OCI runtime-spec <c>LinuxSeccomp</c> struct: all containerd keeps when it decodes a Localhost profile.</summary>
    private static readonly string[] OciProfileKeys = { "defaultAction", "defaultErrnoRet", "architectures", "flags", "listenerPath", "listenerMetadata", "syscalls" };

    /// <summary>The fields of the OCI <c>LinuxSyscall</c> struct, plus <c>comment</c>, which Docker, containerd and CRI-O all ignore.</summary>
    private static readonly string[] OciRuleKeys = { "names", "action", "errnoRet", "args", "comment" };

    /// <summary>SHA-256 of <c>codespace-worker.json</c> as <c>derive-codespace-worker.sh</c> writes it, with LF line endings.</summary>
    private const string ReviewedProfileSha256 = "40a59e30e31b4e74b1db959883e0f5c2c678753f4374f09e67755b82fcd55844";

    /// <summary>What bubblewrap calls to build its sandbox, which moby's default profile reserves for CAP_SYS_ADMIN (or, for <c>pivot_root</c>, denies outright).</summary>
    [Theory]
    [InlineData("clone")]
    [InlineData("clone3")]
    [InlineData("mount")]
    [InlineData("pivot_root")]
    [InlineData("setns")]
    [InlineData("umount2")]
    [InlineData("unshare")]
    public void The_worker_profile_allows_what_bubblewrap_needs_without_a_capability(string syscall)
    {
        var rules = ProfileRules().Where(rule => Names(rule).Contains(syscall)).ToList();

        rules.ShouldContain(rule => IsUnconditionalAllow(rule), $"{syscall} must be allowed for a worker with no capabilities; without it bubblewrap cannot build its sandbox and every run is unconfined");
        rules.ShouldAllBe(rule => Action(rule) == "SCMP_ACT_ALLOW", $"a rule that denies {syscall} beside the one that allows it leaves the outcome to the runtime's rule merge");
    }

    [Fact]
    public void The_worker_profile_still_denies_what_it_does_not_list() =>
        ReadProfile().GetProperty("defaultAction").GetString().ShouldBe("SCMP_ACT_ERRNO", "the profile is moby's default plus bubblewrap's calls, not an allow-list turned inside out");

    [Fact]
    public void The_worker_profile_allows_nothing_else_moby_reserves_for_a_capability()
    {
        var allowed = ProfileRules().Where(rule => Action(rule) == "SCMP_ACT_ALLOW").SelectMany(Names);

        allowed.Intersect(CapabilityGatedSyscalls).ShouldBeEmpty("only bubblewrap's calls leave moby's capability-gated set; the worker holds no capability, so the rest stay denied");
    }

    [Fact]
    public void Every_runtime_reads_the_worker_profile_alike()
    {
        var profileKeys = ReadProfile().EnumerateObject().Select(property => property.Name);
        var ruleKeys = ProfileRules().SelectMany(rule => rule.EnumerateObject()).Select(property => property.Name).Distinct();

        profileKeys.Except(OciProfileKeys).ShouldBeEmpty("containerd decodes a Localhost profile into the OCI LinuxSeccomp struct and drops any other key (moby's archMap among them), so the file must say everything in OCI's fields");
        ruleKeys.Except(OciRuleKeys).ShouldBeEmpty("containerd drops a rule's includes and excludes, so a rule moby gates on a capability becomes an unconditional allow there; the profile must carry no condition only some runtimes evaluate");
    }

    [Fact]
    public void The_worker_profile_is_the_reviewed_derivation()
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(ProfilePath()).ReplaceLineEndings("\n"))));

        actual.ShouldBe(ReviewedProfileSha256, "codespace-worker.json is the output of backend/deploy/seccomp/derive-codespace-worker.sh; change what the worker may call there, re-run it, and update this pin in the same diff");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_boot_posture_line_names_each_probe_a_launch_reads(bool helperPresent)
    {
        // /bin/sh stands in for a present helper on the POSIX hosts the suite runs on; Windows gets any present file.
        var helperPath = helperPresent ? (OperatingSystem.IsWindows() ? Environment.ProcessPath! : "/bin/sh") : "/nonexistent/codespace-mcp";
        var logger = new CapturingLogger();

        LocalProcessRunner.LogSandboxPosture(logger, helperPath);

        var line = logger.Entries.ShouldHaveSingleItem("the posture is one line, so an operator greps one line");
        line.Level.ShouldBe(LogLevel.Information);
        line.Properties["BubblewrapConfines"].ShouldBe(BubblewrapSandbox.Available is not null);
        line.Properties["BubblewrapUnavailableReason"].ShouldBe(BubblewrapSandbox.UnavailableReason ?? "none");
        line.Properties["McpProxyPresent"].ShouldBe(helperPresent);
        line.Properties["McpProxyPath"].ShouldBe(helperPath);
        line.Properties["CanFilter"].ShouldBe(FilteredEgressNetns.CanFilter);
        line.Properties["FilterUnavailableReason"].ShouldBe(FilteredEgressNetns.FilterUnavailableReason ?? "none");
    }

    /// <summary>Allowed for every caller on every architecture: no argument filter, and no capability, architecture or kernel condition.</summary>
    private static bool IsUnconditionalAllow(JsonElement rule) =>
        Action(rule) == "SCMP_ACT_ALLOW" && IsEmpty(rule, "args") && IsEmpty(rule, "includes") && IsEmpty(rule, "excludes");

    private static bool IsEmpty(JsonElement rule, string property)
    {
        if (!rule.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return true;

        return value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() == 0 : !value.EnumerateObject().Any();
    }

    private static string? Action(JsonElement rule) => rule.GetProperty("action").GetString();

    private static IEnumerable<string> Names(JsonElement rule) => rule.GetProperty("names").EnumerateArray().Select(name => name.GetString()!);

    private static IReadOnlyList<JsonElement> ProfileRules() => ReadProfile().GetProperty("syscalls").EnumerateArray().ToList();

    private static JsonElement ReadProfile()
    {
        using var profile = JsonDocument.Parse(File.ReadAllText(ProfilePath()));

        return profile.RootElement.Clone();
    }

    private static string ProfilePath() => LocateRepoFile("backend", "deploy", "seccomp", "codespace-worker.json");

    private static string LocateRepoFile(params string[] segments)
    {
        var relative = Path.Combine(segments);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"{relative} not found walking up from {AppContext.BaseDirectory}");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, IReadOnlyDictionary<string, object?> Properties)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
