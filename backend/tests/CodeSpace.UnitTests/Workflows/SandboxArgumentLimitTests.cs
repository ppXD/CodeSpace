using System.Text;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins <see cref="SandboxArgumentLimit"/> — the preflight that refuses an invocation the kernel will not accept.
///
/// <para>Linux caps a SINGLE argv/envp string at <c>MAX_ARG_STRLEN = 32 * PAGE_SIZE</c>
/// (<c>include/uapi/linux/binfmts.h</c>, enforced in <c>fs/exec.c copy_strings()</c> via <c>valid_arg_len()</c>), and
/// because <c>strnlen_user</c> counts the NUL the largest CONTENT is one byte below that. The cap is independent of
/// the ~2 MiB total argv+envp budget, which is what <c>getconf ARG_MAX</c> reports — so an invocation that clears the
/// total by 13x still dies at the per-string wall. Without this preflight a >128 KiB agent goal reached
/// <c>execve</c>, failed with E2BIG, and — because the supervisor shell never ran to write an exit marker — surfaced
/// as "exited with code -1 (no exit marker — the process vanished, likely an external/OOM kill or host teardown)",
/// which named the wrong subsystem and was retried forever.</para>
///
/// <para>macOS has NO per-string cap at all (measured: a 200000-byte single argument execs cleanly; only the
/// 1048576-byte SHARED total binds), so this failure is structurally unreproducible on a dev host. That is why the
/// limit falls back to the strictest real Linux value off Linux rather than to the local kernel's.</para>
/// </summary>
public sealed class SandboxArgumentLimitTests
{
    private static SandboxSpec SpecWith(params string[] args) => new() { Command = "/bin/echo", Args = args };

    private static string OfBytes(int count) => new('x', count);

    [Fact]
    public void The_limit_is_131071_bytes_on_every_4_KiB_page_host()
    {
        // A 16 KiB / 64 KiB-page arm64 kernel legitimately accepts more; the guard computes, so it stays correct
        // there. This pin is the number that governs the documented deployment (Dockerfile.worker, Debian, 4 KiB).
        if (OperatingSystem.IsLinux() && System.Environment.SystemPageSize != 4096) return;

        SandboxArgumentLimit.MaxStringBytes.ShouldBe(131071,
            customMessage: "MAX_ARG_STRLEN is 32 x PAGE_SIZE and strnlen_user counts the NUL, so 131071 content bytes is the most one argv/envp string can carry");
    }

    [Fact]
    public void A_non_linux_host_uses_the_strictest_linux_value_so_dev_refuses_what_production_refuses()
    {
        if (OperatingSystem.IsLinux()) return;

        SandboxArgumentLimit.MaxStringBytes.ShouldBe(32 * 4096 - 1,
            customMessage: "macOS enforces no per-string cap, so a dev host that trusted its own page size would accept what every 4 KiB-page worker refuses — the exact divergence that let this ship");
    }

    [Theory]
    [InlineData(0)]     // exactly at the cap
    [InlineData(-1)]    // one under
    [InlineData(-5000)]
    public void An_argument_that_fits_is_accepted(int delta)
    {
        SandboxArgumentLimit.Exceeded(SpecWith(OfBytes(SandboxArgumentLimit.MaxStringBytes + delta))).ShouldBeNull();
    }

    [Fact]
    public void One_byte_over_the_cap_is_refused()
    {
        var offender = SandboxArgumentLimit.Exceeded(SpecWith(OfBytes(SandboxArgumentLimit.MaxStringBytes + 1)));

        offender.ShouldNotBeNull(customMessage: "the cliff is exact — there is no degradation between 131071 and 131072");
    }

    [Fact]
    public void The_refusal_names_the_actual_size_the_limit_and_the_position()
    {
        var oversized = SandboxArgumentLimit.MaxStringBytes + 1;

        var offender = SandboxArgumentLimit.Exceeded(SpecWith("--print", "--model", "opus", OfBytes(oversized)))!;

        offender.ShouldContain(oversized.ToString(), customMessage: "an operator cannot shorten a prompt without knowing how far over it is");
        offender.ShouldContain(SandboxArgumentLimit.MaxStringBytes.ToString());
        offender.ShouldContain("4", customMessage: "the offending position — the goal is the 4th argument here");
    }

    [Fact]
    public void The_refusal_says_this_is_not_a_memory_limit()
    {
        // The whole cost of this bug was a message that named the wrong subsystem. A reader who lands here must not
        // go raise a memory ceiling: no process was created and no memory was ever allocated.
        var offender = SandboxArgumentLimit.Exceeded(SpecWith(OfBytes(SandboxArgumentLimit.MaxStringBytes + 1)))!;

        offender.ShouldContain("not a memory limit", Case.Insensitive);
        offender.ShouldNotContain("OOM", Case.Insensitive);
    }

    [Fact]
    public void The_count_is_utf8_bytes_not_characters()
    {
        // A CJK-heavy goal crosses the wall at roughly a third of the character count, so a string.Length check
        // would wave this through and the kernel would refuse it. This is the regression most likely to come back.
        var justUnderInCharacters = new string('界', SandboxArgumentLimit.MaxStringBytes / 2);

        Encoding.UTF8.GetByteCount(justUnderInCharacters).ShouldBeGreaterThan(SandboxArgumentLimit.MaxStringBytes,
            customMessage: "fixture check: this string must be under the cap in CHARACTERS and over it in BYTES");
        justUnderInCharacters.Length.ShouldBeLessThan(SandboxArgumentLimit.MaxStringBytes);

        SandboxArgumentLimit.Exceeded(SpecWith(justUnderInCharacters)).ShouldNotBeNull();
    }

    [Fact]
    public void An_oversized_command_is_refused()
    {
        var spec = new SandboxSpec { Command = "/bin/" + OfBytes(SandboxArgumentLimit.MaxStringBytes) };

        SandboxArgumentLimit.Exceeded(spec).ShouldNotBeNull(customMessage: "argv[0] is copied by the same kernel loop as every other string");
    }

    [Fact]
    public void An_oversized_environment_value_is_refused_by_name_and_never_by_value()
    {
        var secret = "sk-" + OfBytes(SandboxArgumentLimit.MaxStringBytes);
        var spec = new SandboxSpec { Command = "/bin/echo", Environment = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = secret } };

        var offender = SandboxArgumentLimit.Exceeded(spec)!;

        offender.ShouldContain("ANTHROPIC_API_KEY", customMessage: "the operator needs to know WHICH value is oversized");
        offender.ShouldNotContain(secret, customMessage: "a refusal is host metadata; an environment VALUE can be a credential and must never reach an error surface");
        offender.ShouldNotContain("sk-", customMessage: "not even a prefix of the value");
    }

    [Fact]
    public void An_environment_NAME_plus_value_is_measured_as_the_one_string_the_kernel_copies()
    {
        // envp entries are copied as "NAME=VALUE", so a value that fits alone can still overflow once its name and
        // the '=' are counted. Measuring the value in isolation would pass a launch the kernel then refuses.
        var name = "CODESPACE_LONG_VARIABLE_NAME";
        var value = OfBytes(SandboxArgumentLimit.MaxStringBytes - name.Length);
        var spec = new SandboxSpec { Command = "/bin/echo", Environment = new Dictionary<string, string> { [name] = value } };

        SandboxArgumentLimit.Exceeded(spec).ShouldNotBeNull(customMessage: $"NAME=VALUE is {name.Length + 1 + value.Length} bytes, one over the cap, although the value alone fits");
    }

    [Fact]
    public void A_spec_whose_every_string_fits_is_accepted_unchanged()
    {
        var spec = new SandboxSpec
        {
            Command = "/usr/local/bin/claude",
            Args = new[] { "--print", "--output-format", "stream-json", "--verbose", "Fix the failing billing tests" },
            Environment = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-ant-short", ["CLAUDE_CONFIG_DIR"] = "/var/lib/codespace/spool/abc/agent-home" },
        };

        SandboxArgumentLimit.Exceeded(spec).ShouldBeNull(customMessage: "an ordinary run must pay nothing for this guard");
    }
}
