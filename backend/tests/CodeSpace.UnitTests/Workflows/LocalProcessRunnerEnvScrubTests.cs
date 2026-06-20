using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The environment-scrub the local runner ALWAYS applies: an untrusted agent child must NOT inherit the
/// worker's secret-bearing env (DB / Redis / OAuth / the variable master key) — only
/// <see cref="LocalProcessRunner.EnvAllowlist"/> plus the spec's own injected env survive. These exercise the
/// real production path on a real <see cref="ProcessStartInfo"/> (and one real child process).
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalProcessRunnerEnvScrubTests
{
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
    public void Drops_non_allowlisted_inherited_secrets_but_keeps_PATH_and_injected()
    {
        var sentinel = "CODESPACE_SCRUB_SENTINEL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(sentinel, "db-connection-string");
        try
        {
            var info = LocalProcessRunner.BuildStartInfo(EnvSpec());

            info.Environment.ShouldNotContainKey(sentinel);     // the leak is closed
            info.Environment.ShouldContainKey("PATH");          // process can still find its tools
            info.Environment["INJECTED"].ShouldBe("ok");        // injected creds survive the scrub
        }
        finally { Environment.SetEnvironmentVariable(sentinel, null); }
    }

    [Fact]
    public void Lets_an_injected_value_override_an_allowlisted_inherited_one()
    {
        // PATH is allow-listed AND supplied by the spec — the spec (injected) value must win.
        var spec = EnvSpec() with { Environment = new Dictionary<string, string> { ["INJECTED"] = "ok", ["PATH"] = "/sandbox/only" } };

        var info = LocalProcessRunner.BuildStartInfo(spec);

        info.Environment["PATH"].ShouldBe("/sandbox/only");
    }

    [Fact]
    public async Task A_real_child_process_cannot_read_a_worker_secret()
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

            using var process = Process.Start(LocalProcessRunner.BuildStartInfo(spec))!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            stdout.ShouldContain("INJECTED_KEY=from-spec");    // injected cred reaches the child
            stdout.ShouldContain("PATH=");                      // essentials survive
            stdout.ShouldNotContain(sentinel);                  // the worker secret's NAME never reaches the child
            stdout.ShouldNotContain("worker-secret");           // nor its VALUE
        }
        finally { Environment.SetEnvironmentVariable(sentinel, null); }
    }

    // ── C1: non-interactive env defaults (a nested tool's prompt auto-defaults instead of hanging) ────────────────

    [Fact]
    public void NonInteractiveEnv_defaults_pinned() =>
        // Each entry makes a common toolchain non-interactive. Changing the set changes how every agent child behaves
        // toward prompts — pin it so it's a visible, reviewed decision (Rule 8).
        NonInteractiveEnv.Defaults.ShouldBe(new Dictionary<string, string>
        {
            ["CI"] = "1",
            ["DEBIAN_FRONTEND"] = "noninteractive",
            ["DEBCONF_NONINTERACTIVE_SEEN"] = "true",
            ["NPM_CONFIG_YES"] = "true",
            ["PIP_NO_INPUT"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
        }, ignoreOrder: true);

    [Fact]
    public void Injects_the_non_interactive_defaults_into_the_scrubbed_child_env()
    {
        var info = LocalProcessRunner.BuildStartInfo(EnvSpec());

        foreach (var (key, value) in NonInteractiveEnv.Defaults)
            info.Environment[key].ShouldBe(value, $"{key} is injected so a nested tool takes its non-interactive default");

        info.Environment["INJECTED"].ShouldBe("ok", "the spec's own env still survives alongside the defaults");
    }

    [Fact]
    public void An_explicit_spec_value_overrides_a_non_interactive_default()
    {
        // Operator intent wins: a task that deliberately sets one of these (e.g. to re-enable a prompt path) is honoured.
        var spec = EnvSpec() with { Environment = new Dictionary<string, string> { ["INJECTED"] = "ok", ["GIT_TERMINAL_PROMPT"] = "1" } };

        LocalProcessRunner.BuildStartInfo(spec).Environment["GIT_TERMINAL_PROMPT"].ShouldBe("1", "an explicit spec value overrides the non-interactive default");
    }

    [Fact]
    public async Task A_real_child_process_sees_the_non_interactive_defaults()
    {
        if (OperatingSystem.IsWindows()) return;   // uses `env`; the dict-level tests cover the logic cross-platform

        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "env" }, TimeoutSeconds = 30 };

        using var process = Process.Start(LocalProcessRunner.BuildStartInfo(spec))!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        stdout.ShouldContain("CI=1");
        stdout.ShouldContain("DEBIAN_FRONTEND=noninteractive");
        stdout.ShouldContain("GIT_TERMINAL_PROMPT=0");
    }

    [Fact]
    public async Task A_child_that_branches_on_the_prompt_takes_the_non_interactive_path_and_never_hangs()
    {
        // Gate #6: a child that WOULD block on a prompt instead takes its non-interactive branch because CI=1 is
        // injected, so it finishes immediately rather than sitting until the wall-clock timeout. The teeth are REAL: the
        // `else` branch reads from a FIFO that NOBODY writes — opening a FIFO for read blocks until a writer appears —
        // so if the injection were missing the child would genuinely HANG there until the short timeout fired
        // (TimedOut), exactly the silent hang C1 exists to prevent. With C1 present it takes the echo branch and never
        // opens the FIFO → fast Success.
        if (OperatingSystem.IsWindows()) return;

        var fifo = Path.Combine(Path.GetTempPath(), "cs-c1-fifo-" + Guid.NewGuid().ToString("N"));
        using (var mkfifo = Process.Start("mkfifo", fifo)!) await mkfifo.WaitForExitAsync();
        try
        {
            var spec = new SandboxSpec
            {
                Command = "/bin/sh",
                Args = new[] { "-c", $"if [ \"$CI\" = \"1\" ]; then echo NONINTERACTIVE; else read line < {fifo}; fi" },
                TimeoutSeconds = 5,
            };

            var result = await new LocalProcessRunner().RunAsync(spec, CancellationToken.None);

            result.Status.ShouldBe(SandboxStatus.Success, "CI=1 makes the child take the non-interactive branch — it never blocks on the no-writer FIFO until the timeout (a missing injection would TimedOut here)");
            result.Stdout.ShouldContain("NONINTERACTIVE");
        }
        finally { File.Delete(fifo); }
    }

    private static SandboxSpec EnvSpec() => new()
    {
        Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
        Environment = new Dictionary<string, string> { ["INJECTED"] = "ok" },
    };
}
