using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The environment-scrub the local runner applies when <see cref="LocalProcessRunner.EnvScrubEnvVar"/> is on:
/// an untrusted agent child must NOT inherit the worker's secret-bearing env (DB / Redis / OAuth / the variable
/// master key). The scrub decision is threaded into the internal <c>BuildStartInfo(spec, scrub)</c> overload, so
/// these tests exercise the real production code path on a real <see cref="ProcessStartInfo"/> (and one real
/// child process) WITHOUT mutating the process-global flag — keeping them pollution-free under parallel runs.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalProcessRunnerEnvScrubTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseEnvScrubFlag_only_1_or_true_enables(string? raw, bool expected) =>
        LocalProcessRunner.ParseEnvScrubFlag(raw).ShouldBe(expected);

    [Fact]
    public void EnvScrubEnvVar_name_pinned() =>
        LocalProcessRunner.EnvScrubEnvVar.ShouldBe("CODESPACE_AGENT_ENV_SCRUB");

    [Fact]
    public void EnvAllowlist_contents_pinned() =>
        // Each name here is one inherited worker variable a scrubbed agent is still allowed to see. Adding one
        // widens the trust surface — a deliberate, reviewed decision. Pin the exact set so it can't drift silently.
        LocalProcessRunner.EnvAllowlist.ShouldBe(new[]
        {
            "PATH", "HOME", "USER", "LOGNAME", "SHELL", "TERM",
            "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "TZ",
            "TMPDIR", "TEMP", "TMP",
            "SystemRoot", "windir", "ComSpec", "PATHEXT", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
            "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy",
            "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE",
        });

    [Fact]
    public void Scrub_off_keeps_the_full_inherited_environment()
    {
        var sentinel = "CODESPACE_SCRUB_SENTINEL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(sentinel, "secret-value");
        try
        {
            var info = LocalProcessRunner.BuildStartInfo(EnvSpec(), scrub: false);

            info.Environment.ShouldContainKey(sentinel);        // v0 behaviour preserved — non-breaking default
            info.Environment["INJECTED"].ShouldBe("ok");        // spec env still layered on
        }
        finally { Environment.SetEnvironmentVariable(sentinel, null); }
    }

    [Fact]
    public void Scrub_on_drops_non_allowlisted_inherited_secrets_but_keeps_PATH_and_injected()
    {
        var sentinel = "CODESPACE_SCRUB_SENTINEL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(sentinel, "db-connection-string");
        try
        {
            var info = LocalProcessRunner.BuildStartInfo(EnvSpec(), scrub: true);

            info.Environment.ShouldNotContainKey(sentinel);     // the leak is closed
            info.Environment.ShouldContainKey("PATH");          // process can still find its tools
            info.Environment["INJECTED"].ShouldBe("ok");        // injected creds survive the scrub
        }
        finally { Environment.SetEnvironmentVariable(sentinel, null); }
    }

    [Fact]
    public void Scrub_on_lets_an_injected_value_override_an_allowlisted_inherited_one()
    {
        // PATH is allow-listed AND supplied by the spec — the spec (injected) value must win.
        var spec = EnvSpec() with { Environment = new Dictionary<string, string> { ["INJECTED"] = "ok", ["PATH"] = "/sandbox/only" } };

        var info = LocalProcessRunner.BuildStartInfo(spec, scrub: true);

        info.Environment["PATH"].ShouldBe("/sandbox/only");
    }

    [Fact]
    public async Task Scrub_on_a_real_child_process_cannot_read_a_worker_secret()
    {
        if (OperatingSystem.IsWindows()) return;   // uses `env`; the dict-level tests cover the logic cross-platform

        var sentinel = "CODESPACE_SCRUB_SENTINEL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(sentinel, "worker-secret");
        try
        {
            var spec = new SandboxSpec
            {
                Command = "/bin/sh",
                Args = new[] { "-c", "env" },
                Environment = new Dictionary<string, string> { ["INJECTED_KEY"] = "from-spec" },
                TimeoutSeconds = 30,
            };

            using var process = Process.Start(LocalProcessRunner.BuildStartInfo(spec, scrub: true))!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            stdout.ShouldContain("INJECTED_KEY=from-spec");    // injected cred reaches the child
            stdout.ShouldContain("PATH=");                      // essentials survive
            stdout.ShouldNotContain(sentinel);                  // the worker secret's NAME never reaches the child
            stdout.ShouldNotContain("worker-secret");           // nor its VALUE
        }
        finally { Environment.SetEnvironmentVariable(sentinel, null); }
    }

    private static SandboxSpec EnvSpec() => new()
    {
        Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
        Environment = new Dictionary<string, string> { ["INJECTED"] = "ok" },
    };
}
