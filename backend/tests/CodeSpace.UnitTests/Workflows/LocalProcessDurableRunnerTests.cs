using CodeSpace.Core.Settings;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The DURABLE half of <see cref="LocalProcessRunner"/> (<see cref="ISandboxDurableRunner"/>) against REAL
/// OS processes: launch under the /bin/sh spool supervisor, tail the spool, complete by the exit marker.
/// The defining behaviours are (a) cancelling the attach stops observing WITHOUT killing the process — the
/// hinge that lets a restarted backend recover the run — and (b) a fresh attach to an already-exited run
/// still recovers its full output from the spool. POSIX-only (the supervisor is /bin/sh), so each test
/// skips on Windows (Rule 12.1). Spool dirs are GUID-keyed + cleaned up (Rule 12.2/12.3).
/// </summary>
[Trait("Category", "Unit")]
[Collection("LocalProcessIdleWatchdog")]
public sealed class LocalProcessDurableRunnerTests : IDisposable
{
    private readonly LocalProcessRunner _runner = new();
    private readonly List<string> _spoolDirs = new();

    private async Task<SandboxHandle> LaunchAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        var handle = await _runner.LaunchAsync(spec, Guid.NewGuid().ToString("N"), ct);
        _spoolDirs.Add(handle.SpoolDirectory);
        return handle;
    }

    private async Task<(SandboxResult Result, List<string> Lines)> AttachCollectAsync(SandboxHandle handle, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var result = await _runner.AttachAsync(handle, (l, _) => { lines.Add(l.Trim()); return Task.CompletedTask; }, ct);
        return (result, lines);
    }

    [Fact]
    public async Task Launches_a_supervised_process_and_records_a_handle()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.Print("hi"));

        handle.Kind.ShouldBe("local");
        handle.ProcessId.ShouldBeGreaterThan(0);
        Directory.Exists(handle.SpoolDirectory).ShouldBeTrue("the spool directory is created at launch");
        handle.Deadline.ShouldBeGreaterThan(DateTimeOffset.UtcNow, "the wall-clock deadline is in the future");
    }

    [Fact]
    public async Task Probe_treats_a_recycled_pid_as_gone_when_the_recorded_start_time_no_longer_matches()
    {
        if (OperatingSystem.IsWindows()) return;

        // The PID-reuse guard: across a restart the OS can hand our old pid to an unrelated process. A handle
        // bearing that pid but a start time that no longer matches the live process is NOT our run.
        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 });
        handle.ProcessStartTimeUtc.ShouldNotBeNull();

        (await _runner.ProbeAsync(handle, default)).State.ShouldBe(SandboxRunState.Running, "the live supervisor with its matching start time probes Running");

        var recycled = handle with { ProcessStartTimeUtc = handle.ProcessStartTimeUtc!.Value.AddMinutes(-30) };
        (await _runner.ProbeAsync(recycled, default)).State.ShouldBe(SandboxRunState.Gone, "the same pid with a mismatched recorded start time is a recycled pid, not our run");

        KillTree(handle.ProcessId);
    }

    [Fact]
    public async Task An_older_handle_without_a_recorded_start_time_still_probes_running()
    {
        if (OperatingSystem.IsWindows()) return;

        // Back-compat: a handle persisted before the PID-reuse guard existed has no start time → the guard is
        // skipped and liveness alone decides, so an in-flight run from an older backend is never wrongly abandoned.
        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 }) with { ProcessStartTimeUtc = null };

        (await _runner.ProbeAsync(handle, default)).State.ShouldBe(SandboxRunState.Running);

        KillTree(handle.ProcessId);
    }

    [Fact]
    public async Task Attach_streams_lines_in_order_then_completes_success_with_an_exit_marker()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.MultiLine("alpha", "beta", "gamma"));

        var (result, lines) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.Success);
        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe("", "stdout is delivered live via the callback, not accumulated");
        lines.ShouldBe(new[] { "alpha", "beta", "gamma" });
        File.ReadAllText(Path.Combine(handle.SpoolDirectory, "exit")).Trim().ShouldBe("0", "the supervisor records the exit code in the marker");
    }

    [Fact]
    public async Task Nonzero_exit_completes_failed_with_the_code()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.PrintThenExit("partial", 2));

        var (result, lines) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.Failed);
        result.ExitCode.ShouldBe(2);
        lines.ShouldContain("partial", "what the process printed before exiting is still observed");
    }

    [Fact]
    public async Task Stderr_is_captured_from_the_spool_with_no_stdout_lines()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.PrintToStderr("err-on-spool"));

        var (result, lines) = await AttachCollectAsync(handle);

        result.Stderr.ShouldContain("err-on-spool");
        lines.ShouldBeEmpty();
    }

    /// <summary>A run whose whole output is diagnostics — built here rather than through the shared cross-platform builders, because the tests below are POSIX-guarded and need a shell loop those builders do not express.</summary>
    private static SandboxSpec Diagnostics(string script) => new() { Command = "/bin/sh", Args = new[] { "-c", script }, TimeoutSeconds = 30 };

    /// <summary>Drain a source under the given budget, collecting what it delivers. The byte budget defaults past any fixture here, so a test that does not name one is testing the line budget alone.</summary>
    private async Task<(IReadOnlyList<SandboxDiagnosticLine> Delivered, long Advanced)> DrainAsync(SandboxHandle handle, long fromOffset, int maxLines, int maxBytes = 64 * 1024 * 1024)
    {
        var delivered = new List<SandboxDiagnosticLine>();
        var budget = new SandboxDiagnosticBudget { MaxLines = maxLines, MaxBytes = maxBytes };
        var advanced = await ((ISandboxDurableDiagnosticSource)_runner).DrainDiagnosticsAsync(handle, fromOffset, budget, (line, _) => { delivered.Add(line); return Task.CompletedTask; }, CancellationToken.None);

        return (delivered, advanced);
    }

    /// <summary>Just the text of what a drain delivered, for the cases whose subject is the lines and not their completeness.</summary>
    private static IReadOnlyList<string> Texts(IReadOnlyList<SandboxDiagnosticLine> delivered) => delivered.Select(line => line.Text).ToArray();

    /// <summary>
    /// The diagnostic sibling: the spooled stderr delivered LINE BY LINE. This is what makes a harness's own
    /// diagnostics durable — they used to be read whole into one string on every terminal path and then dropped on the
    /// floor by the executor's mapping, so they survived only as long as the spool did.
    /// </summary>
    [Fact]
    public async Task Diagnostics_are_delivered_line_by_line_from_the_spool()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(Diagnostics("printf 'warn one\\nwarn two\\n' >&2"));
        await AttachCollectAsync(handle);

        var (delivered, advanced) = await DrainAsync(handle, 0, maxLines: 64);

        Texts(delivered).ShouldBe(new[] { "warn one", "warn two" });
        delivered.ShouldAllBe(line => line.IsComplete, "the source terminated both, so neither is a cut the caller has to record as half a frame");
        advanced.ShouldBe(new FileInfo(Path.Combine(handle.SpoolDirectory, "err.log")).Length,
            customMessage: "the drain answers where the next one resumes, so a second drain of the same process delivers nothing rather than everything again");

        var second = await DrainAsync(handle, advanced, maxLines: 64);

        second.Advanced.ShouldBe(advanced);
        second.Delivered.ShouldBeEmpty();
    }

    /// <summary>
    /// The drain is BOUNDED by the caller's budget, and the bound is what keeps a round's completion path a constant
    /// rather than a function of how much stderr the run produced. A hundred-megabyte <c>set -x</c> trace must not be
    /// able to hold a computed-but-unmapped result behind a row-per-line write.
    ///
    /// <para>An exhausted budget is a POSITION, not a loss: the answered offset is where the next drain resumes, and
    /// resuming there delivers exactly the lines the first one stopped short of.</para>
    /// </summary>
    [Fact]
    public async Task The_drain_stops_at_the_callers_budget_and_answers_where_to_resume()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(Diagnostics("i=0; while [ $i -lt 12 ]; do printf 'warn %d\\n' $i >&2; i=$((i+1)); done"));
        await AttachCollectAsync(handle);

        var (first, advanced) = await DrainAsync(handle, 0, maxLines: 5);

        Texts(first).ShouldBe(new[] { "warn 0", "warn 1", "warn 2", "warn 3", "warn 4" },
            customMessage: "the budget is a hard stop, not a hint: without it one line of stderr is one durable row and the drain is as long as the run was chatty");
        advanced.ShouldBe(first.Sum(line => line.Text.Length + 1),
            customMessage: "the answered offset must cover exactly the lines delivered — over-claiming it silently drops the remainder, under-claiming it re-delivers a line the caller already recorded");

        var (rest, _) = await DrainAsync(handle, advanced, maxLines: 64);

        Texts(rest).ShouldBe(Enumerable.Range(5, 7).Select(index => $"warn {index}").ToArray(),
            customMessage: "an exhausted budget parks the remainder at a resumable position rather than losing it");
    }

    /// <summary>
    /// A line longer than one read pass must never be delivered as two. The reader works in
    /// <c>MaxReadChunk</c>-bounded passes, so a line straddling that boundary would otherwise be cut at an arbitrary
    /// byte — two records for one diagnostic, each side of the cut opening or closing on a half-decoded character.
    ///
    /// <para>Written straight into the spool rather than produced by a shell loop: what is under test is the READER's
    /// chunking, and 24 MiB through a pipe would buy nothing but seconds.</para>
    /// </summary>
    [Fact]
    public async Task A_line_straddling_a_read_chunk_is_delivered_whole()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(Diagnostics("printf 'placeholder\\n' >&2"));
        await AttachCollectAsync(handle);

        // Three 5 MiB lines: the second one spans the 8 MiB chunk boundary, and the third starts past it.
        var written = Enumerable.Range(0, 3).Select(index => new string((char)('a' + index), 5 * 1024 * 1024)).ToArray();
        await File.WriteAllTextAsync(Path.Combine(handle.SpoolDirectory, "err.log"), string.Join('\n', written) + "\n");

        var (delivered, advanced) = await DrainAsync(handle, 0, maxLines: 64);

        Texts(delivered).ShouldBe(written, customMessage: "a line cut at the read-chunk boundary arrives as two records, and the split is a raw byte cut");
        delivered.ShouldAllBe(line => line.IsComplete, "every one of these IS terminated in the source — a straddling line is rejoined by the reader, not reported as a cut");
        advanced.ShouldBe(new FileInfo(Path.Combine(handle.SpoolDirectory, "err.log")).Length);
    }

    /// <summary>
    /// The pure boundary rule the chunked reader turns on, pinned directly because its hard cases need megabytes to
    /// reach through the file: a pass that does not reach the end of the source stops at its LAST newline, and one that
    /// holds no newline at all consumes the pass ANYWAY and reports that it did not end a line.
    ///
    /// <para>That last row is the one that matters. Consuming nothing there is not a delay, it is a permanent stop: the
    /// pass can never hold a newline however often it is retried, so the drain answers the same offset, its caller
    /// reads that as a finished stream, and every diagnostic after one over-long line is unreachable while the run
    /// records a clean drain.</para>
    /// </summary>
    [Theory]
    [InlineData("one\ntwo\nthree", 999, false, 8, true)]
    [InlineData("one\ntwo\nthree", 999, true, 13, true)]
    [InlineData("one\ntwo\nthree", 2, false, 8, true)]
    [InlineData("one\ntwo\nthree", 1, false, 4, true)]
    [InlineData("no newline here", 999, true, 15, true)]
    // A line no pass can terminate: the pass is consumed, and the caller is told it holds part of a line.
    [InlineData("no newline here", 999, false, 15, false)]
    public void A_read_pass_consumes_only_whole_lines_unless_it_reaches_the_end(string text, int maxLines, bool reachesEnd, int expectedBytes, bool expectedEndsLine)
    {
        var buffer = Encoding.UTF8.GetBytes(text);

        var (bytes, endsLine) = LocalProcessRunner.DiagnosticBoundary(buffer, buffer.Length, maxLines, reachesEnd);

        bytes.ShouldBe(expectedBytes,
            customMessage: "a pass that consumes past its last newline while the source continues hands the caller half a line, and one that consumes NOTHING ends the drain at that byte for good");
        endsLine.ShouldBe(expectedEndsLine,
            customMessage: "a cut reported as a whole line becomes two durable diagnostics where the harness wrote one");
    }

    /// <summary>
    /// Where a FORCED cut lands, decided in bytes, so the one case that cannot stop at a newline still cannot open or
    /// close on half a character. The mirror of <see cref="LocalProcessRunner.TailStart"/> at the other end of a buffer,
    /// and pinned the same way — reaching it through a real spool costs megabytes for a question about three bytes.
    ///
    /// <para>The rule is deliberately conservative: it also defers a character that ENDS exactly at the cut, because
    /// telling that from a truncated one costs a length table for three bytes the next pass reads anyway. What it may
    /// never do is answer zero, which is why the all-continuation row is here — a binary blob on a diagnostic stream
    /// has no boundary to find, and a reader that stopped there would strand the rest of the file.</para>
    /// </summary>
    [Theory]
    // Ends on ASCII — nothing to defer.
    [InlineData(new byte[] { 0x61, 0x62 }, 2)]
    [InlineData(new byte[] { 0xe2, 0x98, 0x83, 0x61 }, 4)]
    // Ends inside a 3-byte sequence: back off its continuation bytes and its lead byte.
    [InlineData(new byte[] { 0x61, 0xe2, 0x98 }, 1)]
    [InlineData(new byte[] { 0x61, 0xe2 }, 1)]
    // Ends ON a whole character: deferred to the next pass rather than measured.
    [InlineData(new byte[] { 0x61, 0xe2, 0x98, 0x83 }, 1)]
    // No boundary anywhere: take the pass rather than nothing, because progress is the point.
    [InlineData(new byte[] { 0x83, 0x98 }, 2)]
    public void A_forced_cut_lands_on_a_character_boundary_and_never_on_nothing(byte[] pass, int expected)
    {
        var end = LocalProcessRunner.WholeCharacters(pass, pass.Length);

        end.ShouldBe(expected);
        end.ShouldBeGreaterThan(0,
            customMessage: "a forced cut of zero bytes is the dead stop this method exists to prevent");
    }

    /// <summary>
    /// The defect this closes, end to end: ONE diagnostic longer than a read pass used to end the drain at that byte
    /// permanently and silently — the reader answered the same offset, the drain read that as a finished stream, and
    /// every line after it was lost while the run recorded a clean finish. A harness dumping a stack, a payload or a
    /// minified blob is exactly that input.
    ///
    /// <para>What the over-long line becomes is a deliberate choice: a frame the reader says it CUT, plus its
    /// continuation. Splicing it silently into two complete-looking lines would put two diagnostics into the durable
    /// stream where the harness wrote one.</para>
    ///
    /// <para>Written straight into the spool rather than produced by a shell loop, for the same reason the straddling
    /// fixture above is: what is under test is the READER.</para>
    /// </summary>
    [Fact]
    public async Task A_line_longer_than_a_read_pass_is_cut_forward_rather_than_ending_the_drain()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(Diagnostics("printf 'placeholder\\n' >&2"));
        await AttachCollectAsync(handle);

        // 9 MiB with no line break anywhere in it — longer than the 8 MiB read pass, so no pass can terminate it.
        var overlong = new string('x', 9 * 1024 * 1024);
        await File.WriteAllTextAsync(Path.Combine(handle.SpoolDirectory, "err.log"), overlong + "\nafter-the-long-line\n");

        var (delivered, advanced) = await DrainAsync(handle, 0, maxLines: 64);

        Texts(delivered).ShouldContain("after-the-long-line",
            customMessage: $"the drain delivered {delivered.Count} lines and never reached the diagnostic AFTER the over-long one: it stopped at that byte, and because a stopped drain answers the offset it started from, its caller reads the strand as a finished stream");
        advanced.ShouldBe(new FileInfo(Path.Combine(handle.SpoolDirectory, "err.log")).Length,
            customMessage: "an offset short of the source is a drain that parked without a budget saying so");

        delivered[0].IsComplete.ShouldBeFalse(
            customMessage: "the first piece is half a line, and a caller recording it as whole writes a diagnostic the harness never produced");
        delivered[1].IsComplete.ShouldBeTrue(
            customMessage: "the remainder IS terminated in the source, so it completes the frame the cut opened");
        string.Concat(delivered[0].Text, delivered[1].Text).ShouldBe(overlong,
            customMessage: "the two pieces must rejoin into exactly what the harness wrote, with nothing dropped or doubled at the cut");
    }

    /// <summary>
    /// The BYTE half of the drain's budget. A line budget alone bounds only the row count: two thousand lines of a
    /// megabyte each is two thousand rows and two gigabytes of payload, which is the dimension the caller's delay is
    /// actually paid in. So the budget binds in bytes too, and an exhausted byte budget parks at a resumable position
    /// exactly as an exhausted line budget does.
    /// </summary>
    [Fact]
    public async Task The_drain_stops_at_the_callers_byte_budget_and_answers_where_to_resume()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(Diagnostics("i=0; while [ $i -lt 12 ]; do printf 'warn %d\\n' $i >&2; i=$((i+1)); done"));
        await AttachCollectAsync(handle);

        // Two whole lines of "warn N\n" and not a byte more.
        var (first, advanced) = await DrainAsync(handle, 0, maxLines: 64, maxBytes: 14);

        Texts(first).ShouldBe(new[] { "warn 0", "warn 1" },
            customMessage: "without a byte bound the frame ceiling lets a run write megabytes per row, and the round's outcome waits behind all of it");
        advanced.ShouldBe(14);

        var (rest, _) = await DrainAsync(handle, advanced, maxLines: 64);

        Texts(rest).ShouldBe(Enumerable.Range(2, 10).Select(index => $"warn {index}").ToArray(),
            customMessage: "an exhausted byte budget parks the remainder at a resumable position rather than losing it");
    }

    /// <summary>
    /// The retention fix. The terminal read used to be <c>File.ReadAllTextAsync</c> over the whole spooled stderr — an
    /// accumulator that grew with the run, the same shape removed twice before from the transcript and the event
    /// buffer. It is now a bounded tail, and the bound has to actually bind while staying byte-identical below it.
    /// </summary>
    [Fact]
    public async Task The_buffered_stderr_excerpt_is_bounded_and_cut_at_a_line_boundary()
    {
        if (OperatingSystem.IsWindows()) return;

        // 4000 lines of 40 bytes each ≈ 160 KiB, comfortably past the 64 KiB excerpt cap.
        var handle = await LaunchAsync(Diagnostics("i=0; while [ $i -lt 4000 ]; do printf 'diagnostic-line-%034d\\n' $i >&2; i=$((i+1)); done"));

        var (result, _) = await AttachCollectAsync(handle);

        var spooled = new FileInfo(Path.Combine(handle.SpoolDirectory, "err.log")).Length;
        spooled.ShouldBeGreaterThan(result.Stderr.Length,
            customMessage: $"the spool holds {spooled} bytes and the excerpt {result.Stderr.Length}; if they matched, the terminal read is still pulling the whole stream into memory and still grows with the run");
        result.Stderr.Length.ShouldBeLessThanOrEqualTo(64 * 1024);
        result.Stderr.ShouldEndWith("diagnostic-line-" + 3999.ToString("D34") + "\n",
            customMessage: "a diagnostic excerpt that dropped the END would throw away the last thing a failing process managed to say");
        result.Stderr.Split('\n')[0].ShouldStartWith("diagnostic-line-",
            customMessage: "a tail holding newlines opens after one of them, so the excerpt never opens mid-line — and a newline is a byte boundary in UTF-8, so that cut cannot split a character either");
    }

    /// <summary>
    /// Where the over-cap excerpt begins, decided in BYTES. The tail buffer starts at an arbitrary byte of the source,
    /// so the previous rule — decode the buffer, then cut at the first newline — was only safe for a tail that HAD a
    /// newline. One long JSON dump, a minified stack, a progress stream that never terminates a line: those reach the
    /// same branch and used to be returned as the raw byte cut, decoded from a buffer opening mid-character.
    ///
    /// <para>Pinned on the rule rather than through a terminal path: reaching that branch end-to-end needs a spool
    /// whose last 64 KiB holds no newline, which is minutes of shell for a question about six bytes.</para>
    /// </summary>
    [Theory]
    // A newline anywhere wins: the excerpt opens on a whole line, whatever the buffer began mid-way through.
    [InlineData(new byte[] { 0xa9, 0x0a, 0x61, 0x62 }, 2)]
    [InlineData(new byte[] { 0x0a, 0x61 }, 1)]
    // No newline to cut at, so the cut is the nearest CHARACTER boundary: skip the continuation bytes (10xxxxxx).
    [InlineData(new byte[] { 0xa9, 0x61, 0x62 }, 1)]
    [InlineData(new byte[] { 0x9e, 0x98, 0x83, 0x61 }, 3)]
    // Already on a boundary — nothing to skip, and a lead byte must never be mistaken for a continuation one.
    [InlineData(new byte[] { 0x61, 0x62 }, 0)]
    [InlineData(new byte[] { 0xe2, 0x98, 0x83 }, 0)]
    public void The_over_cap_excerpt_opens_on_a_character_boundary_even_with_no_line_to_cut_at(byte[] tail, int expected)
    {
        var start = LocalProcessRunner.TailStart(tail, tail.Length);

        start.ShouldBe(expected);
        Encoding.UTF8.GetString(tail, start, tail.Length - start).ShouldNotContain("�",
            customMessage: "the excerpt opened inside a UTF-8 sequence, so the first thing a reader sees of a run's diagnostics is replacement noise");
    }

    [Fact]
    public async Task Durable_log_source_reads_bounded_raw_stdout_and_stderr_ranges_without_utf8_boundary_assumptions()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "printf '\\377A'; printf '\\376B' >&2" }, TimeoutSeconds = 30 });
        await AttachCollectAsync(handle);
        var source = (ISandboxDurableLogSource)_runner;
        var descriptors = source.DescribeLogs(handle);
        var stdout = descriptors.Single(value => value.StreamKind == AgentRunLogKinds.StandardOutput);
        var stderr = descriptors.Single(value => value.StreamKind == AgentRunLogKinds.StandardError);

        stdout.ContentType.ShouldBe(AgentRunLogRepresentations.PlainTextContentType);
        stdout.ContentEncoding.ShouldBe(AgentRunLogRepresentations.Utf8ContentEncoding);
        stderr.ContentType.ShouldBe(AgentRunLogRepresentations.PlainTextContentType);
        stderr.ContentEncoding.ShouldBe(AgentRunLogRepresentations.Utf8ContentEncoding);

        var stdoutRead = await source.ReadAsync(new SandboxDurableLogReadRequest { Handle = handle, SourceKey = stdout.SourceKey, OffsetBytes = 0, MinimumBytes = 1, MaximumBytes = 1, FinalDrain = true }, CancellationToken.None);
        var stderrRead = await source.ReadAsync(new SandboxDurableLogReadRequest { Handle = handle, SourceKey = stderr.SourceKey, OffsetBytes = 0, MinimumBytes = 1, MaximumBytes = 2, FinalDrain = true }, CancellationToken.None);
        var unknown = await source.ReadAsync(new SandboxDurableLogReadRequest { Handle = handle, SourceKey = "../out.log", OffsetBytes = 0, MinimumBytes = 1, MaximumBytes = 2, FinalDrain = true }, CancellationToken.None);
        var relative = await source.ReadAsync(new SandboxDurableLogReadRequest { Handle = handle with { SpoolDirectory = "relative-spool" }, SourceKey = stdout.SourceKey, OffsetBytes = 0, MinimumBytes = 1, MaximumBytes = 2, FinalDrain = true }, CancellationToken.None);
        var stdoutEnd = await source.ReadAsync(new SandboxDurableLogReadRequest { Handle = handle, SourceKey = stdout.SourceKey, OffsetBytes = 2, MinimumBytes = 1, MaximumBytes = 2, FinalDrain = true }, CancellationToken.None);

        stdoutRead.ShouldBeOfType<SandboxDurableLogReadResult.Available>().Bytes.Length.ShouldBe(1, "the source obeys the caller's byte cap rather than decoding a character");
        stderrRead.ShouldBeOfType<SandboxDurableLogReadResult.Available>().Bytes.ToArray().ShouldBe(new byte[] { 0xfe, (byte)'B' });
        unknown.ShouldBeOfType<SandboxDurableLogReadResult.Unavailable>().Problem.Code.ShouldBe(SandboxDurableLogProblemCode.UnknownSource, "source keys are an allowlist, never path fragments");
        relative.ShouldBeOfType<SandboxDurableLogReadResult.Unavailable>().Problem.Code.ShouldBe(SandboxDurableLogProblemCode.InvalidRequest, "only the persisted absolute spool root is readable");
        stdoutEnd.ShouldBeOfType<SandboxDurableLogReadResult.EndOfSource>("only a dead and quiescent producer yields the explicit final receipt");
    }

    [Fact]
    public async Task Final_drain_never_treats_an_unsealed_quiescent_spool_as_eof_before_a_late_byte()
    {
        if (OperatingSystem.IsWindows()) return;

        var spool = LocalProcessRunner.SpoolDirectoryFor(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spool);
        _spoolDirs.Add(spool);
        var stdoutPath = Path.Combine(spool, "out.log");
        await File.WriteAllBytesAsync(stdoutPath, []);
        await File.WriteAllBytesAsync(Path.Combine(spool, "err.log"), []);

        using var producer = Process.Start(new ProcessStartInfo { FileName = "/bin/sh", ArgumentList = { "-c", "exit 0" }, UseShellExecute = false })!;
        var producerStart = producer.StartTime.ToUniversalTime();
        await producer.WaitForExitAsync();
        var handle = new SandboxHandle { Kind = LocalProcessRunner.LocalKind, ProcessId = producer.Id, ProcessStartTimeUtc = producerStart, SpoolDirectory = spool, Deadline = DateTimeOffset.MaxValue };
        var source = (ISandboxDurableLogSource)_runner;
        var sourceKey = source.DescribeLogs(handle).Single(value => value.StreamKind == AgentRunLogKinds.StandardOutput).SourceKey;
        var request = new SandboxDurableLogReadRequest { Handle = handle, SourceKey = sourceKey, OffsetBytes = 0, MinimumBytes = 1, MaximumBytes = 32, FinalDrain = true };

        var lateWrite = Task.Run(async () =>
        {
            await Task.Delay(750);
            await File.AppendAllTextAsync(stdoutPath, "late");
        });
        var premature = await source.ReadAsync(request, CancellationToken.None);
        await lateWrite;
        var bytes = await source.ReadAsync(request, CancellationToken.None);
        var end = await source.ReadAsync(request with { OffsetBytes = 4 }, CancellationToken.None);

        premature.ShouldBeOfType<SandboxDurableLogReadResult.NoData>("an empty observation whose size changes during the seal window is transient, never EOF");
        bytes.ShouldBeOfType<SandboxDurableLogReadResult.Available>().Bytes.ToArray().ShouldBe("late"u8.ToArray());
        end.ShouldBeOfType<SandboxDurableLogReadResult.NoData>("time-based quiescence cannot mint EOF without the runner's durable seal authority");
    }

    [Fact]
    public async Task Supervisor_seals_logs_only_after_a_background_descendant_closes_its_output_writer()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "(sleep 0.8; printf late)& exit 0" }, TimeoutSeconds = 30 });
        await Task.Delay(300);

        File.Exists(Path.Combine(handle.SpoolDirectory, "logs.sealed")).ShouldBeFalse("a descendant still holds the FIFO writer, so an apparent command exit cannot mint a false EOF receipt");

        var (result, lines) = await AttachCollectAsync(handle);
        var source = (ISandboxDurableLogSource)_runner;
        var sourceKey = source.DescribeLogs(handle).Single(value => value.StreamKind == AgentRunLogKinds.StandardOutput).SourceKey;
        var end = await source.ReadAsync(new SandboxDurableLogReadRequest { Handle = handle, SourceKey = sourceKey, OffsetBytes = 4, MinimumBytes = 1, MaximumBytes = 32, FinalDrain = true }, CancellationToken.None);

        result.Status.ShouldBe(SandboxStatus.Success);
        lines.ShouldBe(new[] { "late" });
        File.Exists(Path.Combine(handle.SpoolDirectory, "logs.sealed")).ShouldBeTrue("the seal is written only after both FIFO writers reached EOF");
        end.ShouldBeOfType<SandboxDurableLogReadResult.EndOfSource>();
    }

    // ─── Spool size cap: SandboxSpec.MaxFileSizeMb bounds what the FIFO copiers write ─────────────────────────────

    /// <summary>A spec whose command floods stdout with <paramref name="mib"/> MiB (no newlines), then prints a trailing marker and exits with <paramref name="exitCode"/>. Pure shell builtins so it behaves identically under dash and bash.</summary>
    private static SandboxSpec OverflowSpec(int mib, int exitCode) => new()
    {
        Command = "/bin/sh",
        Args = new[] { "-c", $"b=0123456789abcdef; b=$b$b$b$b; b=$b$b$b$b; b=$b$b$b$b; i=0; while [ $i -lt {mib * 1024} ]; do printf %s \"$b\"; i=$((i+1)); done; printf FINALMARKER; exit {exitCode}" },
        TimeoutSeconds = 60,
    };

    [Fact]
    public async Task A_run_flooding_past_the_spool_cap_stops_at_the_cap_and_still_completes_with_its_real_exit_code()
    {
        if (OperatingSystem.IsWindows()) return;

        // The P0: MaxFileSizeMb documented a spool bound that nothing enforced — the FIFO copiers were unbounded
        // `cat`, so a looping agent filled the worker's disk. The copier now stops the file AT the cap while still
        // draining the pipe, so the run terminalizes on its OWN exit code (never TimedOut, never Stalled).
        var handle = await LaunchAsync(OverflowSpec(mib: 4, exitCode: 7) with { MaxFileSizeMb = 1 });

        var (result, _) = await AttachCollectAsync(handle);

        new FileInfo(Path.Combine(handle.SpoolDirectory, "out.log")).Length.ShouldBe(1L * 1024 * 1024,
            "the spool stops AT the cap — 4 MiB of agent chatter must not land 4 MiB on the worker's disk");
        result.Status.ShouldBe(SandboxStatus.Failed, "capping the spool never reinterprets the command's own outcome");
        result.ExitCode.ShouldBe(7, "the exit-code capture path is untouched by the cap — NOT TimedOut, NOT Stalled");
    }

    [Fact]
    public async Task A_run_writing_past_the_spool_cap_is_never_blocked_and_still_seals_its_logs()
    {
        if (OperatingSystem.IsWindows()) return;

        // The hang this design must never cause: a copier that stopped READING at the cap would leave the agent
        // blocked forever writing into a full pipe. The bounded copier drains to EOF and exits normally, so the
        // command reaches its trailing marker, exits 0, `wait` returns, and the host still mints the seal.
        var handle = await LaunchAsync(OverflowSpec(mib: 4, exitCode: 0) with { MaxFileSizeMb = 1 });

        var (result, _) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.Success, "the command ran past the cap to its final marker and exited 0 — a blocked writer would have timed out instead");
        result.ExitCode.ShouldBe(0);
        File.ReadAllText(Path.Combine(handle.SpoolDirectory, "logs.copy-status")).Trim().ShouldBe("0:0", "both bounded copiers exit normally, so `wait` still yields a clean copier status");
        File.Exists(Path.Combine(handle.SpoolDirectory, "logs.sealed")).ShouldBeTrue("draining to real EOF keeps the seal authority intact");
    }

    [Fact]
    public async Task A_copier_that_fails_for_any_reason_other_than_the_budget_withholds_the_seal()
    {
        if (OperatingSystem.IsWindows()) return;

        // The cap must not become a laundry for host failures. Reaching the budget is a DELIBERATE truncation the
        // receipt may claim; a full disk, a read-only spool or an I/O error is a capture failure the seal must
        // withhold. Both used to produce the same clean status, which recorded a failure as a success.
        var spool = Directory.CreateTempSubdirectory("csp-copier-fault-").FullName;
        Directory.CreateDirectory(Path.Combine(spool, "out.log"));   // `cat >out.log` now fails EISDIR — never SIGXFSZ

        var exitCode = await RunSupervisorScriptAsync(spool, maxBytes: 1024 * 1024, command: "printf hello; exit 3");

        exitCode.ShouldBe(0, "the supervisor itself completes — a copier fault must never leave the agent blocked on a full pipe");
        File.ReadAllText(Path.Combine(spool, "exit")).Trim().ShouldBe("3", "the command's own outcome is never reinterpreted by a copier fault");
        File.ReadAllText(Path.Combine(spool, "logs.copy-status")).Trim().ShouldNotBe("0:0", "a non-budget write failure keeps its non-zero copier status, so the seal is withheld");
        File.Exists(Path.Combine(spool, "out.log.truncated")).ShouldBeFalse("only reaching the byte budget mints a truncation marker — a host fault must not masquerade as a deliberate head");
    }

    /// <summary>Drive the supervisor script itself, so the copier's status discrimination is tested at the layer that owns it rather than through a runner lifecycle that cannot stage a write fault.</summary>
    private static async Task<int> RunSupervisorScriptAsync(string spoolDirectory, long maxBytes, string command)
    {
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };

        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(LocalProcessRunner.SupervisorScript);
        info.ArgumentList.Add("sh");
        info.ArgumentList.Add("/bin/sh");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(command);

        info.Environment["CSP_PID"] = Path.Combine(spoolDirectory, "pid");
        info.Environment["CSP_OUT"] = Path.Combine(spoolDirectory, "out.log");
        info.Environment["CSP_ERR"] = Path.Combine(spoolDirectory, "err.log");
        info.Environment["CSP_EXIT"] = Path.Combine(spoolDirectory, "exit");
        info.Environment["CSP_LOG_COPY_STATUS"] = Path.Combine(spoolDirectory, "logs.copy-status");
        info.Environment["CSP_MAX_BYTES"] = maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);

        using var process = Process.Start(info)!;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);   // a blocked copier would hang here — that is the assertion

        return process.ExitCode;
    }

    [Fact]
    public async Task A_run_under_the_spool_cap_spools_byte_identical_output()
    {
        if (OperatingSystem.IsWindows()) return;

        // Non-breaking: under the cap the bounded copier must write exactly what an unbounded `cat` wrote.
        var handle = await LaunchAsync(ContractSpecs.MultiLine("alpha", "beta", "gamma"));

        var (result, lines) = await AttachCollectAsync(handle);

        File.ReadAllBytes(Path.Combine(handle.SpoolDirectory, "out.log")).ShouldBe("alpha\nbeta\ngamma\n"u8.ToArray(), "an under-cap spool is byte-identical to the unbounded copier's");
        result.Status.ShouldBe(SandboxStatus.Success);
        lines.ShouldBe(new[] { "alpha", "beta", "gamma" });
    }

    [Fact]
    public void BuildDurableStartInfo_derives_the_copier_byte_cap_from_the_spec_file_size_knob()
    {
        // The documented knob finally means what it says: MaxFileSizeMb (MiB) becomes the copiers' byte budget.
        var capped = LocalProcessRunner.BuildDurableStartInfo(new SandboxSpec { Command = "mycmd", MaxFileSizeMb = 3 }, "/tmp/spool-cap");
        var uncapped = LocalProcessRunner.BuildDurableStartInfo(new SandboxSpec { Command = "mycmd", MaxFileSizeMb = 0 }, "/tmp/spool-uncap");

        capped.Environment["CSP_MAX_BYTES"].ShouldBe((3L * 1024 * 1024).ToString(), "the copier budget is the spec's MiB knob in bytes");
        uncapped.Environment["CSP_MAX_BYTES"].ShouldBe("0", "0 = unlimited keeps the unbounded copier — byte-identical to before the cap existed");
    }

    [Fact]
    public void Supervisor_script_bounds_the_copiers_without_leaking_the_budget_to_the_child()
    {
        var info = LocalProcessRunner.BuildDurableStartInfo(new SandboxSpec { Command = "mycmd" }, "/tmp/spool-script");
        var script = info.ArgumentList[info.ArgumentList.IndexOf("-c") + 1];

        script.Contains("unset CSP_PID CSP_OUT CSP_ERR CSP_EXIT CSP_LOG_COPY_STATUS CSP_MAX_BYTES", StringComparison.Ordinal)
            .ShouldBeTrue("the child never inherits the copier budget any more than it inherits the spool paths");
        script.Contains("cat >/dev/null", StringComparison.Ordinal)
            .ShouldBeTrue("the bounded copier must keep draining the FIFO after the cap, or the agent blocks forever on a full pipe");
        script.Contains("ulimit -f", StringComparison.Ordinal)
            .ShouldBeTrue("the cap is the kernel's RLIMIT_FSIZE on a plain `cat`, so the spool still writes through immediately for the live tail");
        script.Contains("head -c", StringComparison.Ordinal)
            .ShouldBeFalse("a byte-counting copier would stdio-buffer its output and starve both the live tail and the stall watchdog");
        script.Contains("spool_block=512", StringComparison.Ordinal)
            .ShouldBeTrue("ulimit counts 512-byte blocks under POSIX shells and 1024 under bash — the enforced ceiling must be the same byte count on every host");
    }

    [Fact]
    public async Task Durable_log_source_reports_truncation_only_when_the_spool_reached_its_cap()
    {
        if (OperatingSystem.IsWindows()) return;

        // Truncation is a RECORDED state, not silence: the durable source's final receipt says whether the bytes it
        // just proved complete are the whole source or only the capped head of it.
        var capped = await LaunchAsync(OverflowSpec(mib: 2, exitCode: 0) with { MaxFileSizeMb = 1 });
        await AttachCollectAsync(capped);
        var whole = await LaunchAsync(ContractSpecs.Print("small"));
        await AttachCollectAsync(whole);

        var source = (ISandboxDurableLogSource)_runner;

        var cappedEnd = await source.ReadAsync(EndRequest(source, capped, 1L * 1024 * 1024), CancellationToken.None);
        var wholeEnd = await source.ReadAsync(EndRequest(source, whole, "small\n".Length), CancellationToken.None);

        cappedEnd.ShouldBeOfType<SandboxDurableLogReadResult.EndOfSource>().Truncated.ShouldBeTrue("a spool that reached its cap lost bytes the agent wrote — the receipt must say so");
        wholeEnd.ShouldBeOfType<SandboxDurableLogReadResult.EndOfSource>().Truncated.ShouldBeFalse("an under-cap spool is complete, so its receipt claims no truncation");
    }

    private static SandboxDurableLogReadRequest EndRequest(ISandboxDurableLogSource source, SandboxHandle handle, long offset) => new()
    {
        Handle = handle,
        SourceKey = source.DescribeLogs(handle).Single(value => value.StreamKind == AgentRunLogKinds.StandardOutput).SourceKey,
        OffsetBytes = offset, MinimumBytes = 1, MaximumBytes = 32, FinalDrain = true,
    };

    [Fact]
    public async Task Durable_log_source_rejects_root_escape_dot_segments_and_symlinked_spools_or_files()
    {
        if (OperatingSystem.IsWindows()) return;

        var source = (ISandboxDurableLogSource)_runner;
        var outside = TempDir();
        var outsideLog = Path.Combine(outside, "outside.log");
        await File.WriteAllTextAsync(outsideLog, "must-not-read");
        var template = await LaunchAsync(ContractSpecs.Print("safe"));
        await AttachCollectAsync(template);
        var sourceKey = source.DescribeLogs(template).Single(value => value.StreamKind == AgentRunLogKinds.StandardOutput).SourceKey;
        var request = new SandboxDurableLogReadRequest { Handle = template, SourceKey = sourceKey, OffsetBytes = 0, MinimumBytes = 1, MaximumBytes = 32, FinalDrain = true };

        var outsideRead = await source.ReadAsync(request with { Handle = template with { SpoolDirectory = outside } }, CancellationToken.None);
        var dotSegment = await source.ReadAsync(request with { Handle = template with { SpoolDirectory = Path.Combine(template.SpoolDirectory, "..", Path.GetFileName(template.SpoolDirectory)) } }, CancellationToken.None);

        var linkedSpool = LocalProcessRunner.SpoolDirectoryFor("linked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(linkedSpool)!);
        Directory.CreateSymbolicLink(linkedSpool, outside);
        SandboxDurableLogReadResult linkedSpoolRead;
        try { linkedSpoolRead = await source.ReadAsync(request with { Handle = template with { SpoolDirectory = linkedSpool } }, CancellationToken.None); }
        finally { Directory.Delete(linkedSpool); }

        var spoolLog = Path.Combine(template.SpoolDirectory, "out.log");
        File.Delete(spoolLog);
        File.CreateSymbolicLink(spoolLog, outsideLog);
        var linkedFileRead = await source.ReadAsync(request, CancellationToken.None);

        outsideRead.ShouldBeOfType<SandboxDurableLogReadResult.Unavailable>().Problem.Code.ShouldBe(SandboxDurableLogProblemCode.InvalidRequest, "an absolute path outside the configured spool root is never authorized by a persisted handle");
        dotSegment.ShouldBeOfType<SandboxDurableLogReadResult.Unavailable>().Problem.Code.ShouldBe(SandboxDurableLogProblemCode.InvalidRequest, "a non-canonical persisted path cannot smuggle dot segments through the root clamp");
        linkedSpoolRead.ShouldBeOfType<SandboxDurableLogReadResult.Unavailable>().Problem.Code.ShouldBe(SandboxDurableLogProblemCode.InvalidRequest, "a direct-child symlink can still escape the root and is rejected");
        linkedFileRead.ShouldBeOfType<SandboxDurableLogReadResult.Unavailable>().Problem.Code.ShouldBe(SandboxDurableLogProblemCode.InvalidRequest, "the fixed filename must itself be a real spool file, not a symlink");
    }

    [Fact]
    public async Task Deadline_elapsing_terminates_the_process_and_reports_timed_out()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 1 });

        var (result, _) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.TimedOut, "the observer enforces the handle's wall-clock deadline");
        result.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task A_silent_durable_run_is_terminated_as_stalled_well_before_its_deadline()
    {
        // C3 stall watchdog on the DURABLE (real-run) path: no spool output for the idle window → Stalled, killed early,
        // not left to the far-off deadline. Idle 2s; deadline 30s.
        if (OperatingSystem.IsWindows()) return;

        var prior = Environment.GetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, "2");

            var handle = await LaunchAsync(ContractSpecs.Sleep(30) with { TimeoutSeconds = 30 });

            var (result, _) = await AttachCollectAsync(handle);

            result.Status.ShouldBe(SandboxStatus.Stalled, "no spool advance for the 2s idle window → stalled, not a 30s timeout");
            result.ExitCode.ShouldBe(-1);
        }
        finally { Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, prior); }
    }

    [Fact]
    public async Task A_durable_run_emitting_within_the_idle_window_is_not_stalled()
    {
        // The watchdog must not kill an active durable run: spool advances inside every idle window → runs to completion.
        if (OperatingSystem.IsWindows()) return;

        var prior = Environment.GetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, "2");

            var handle = await LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "for i in 1 2 3 4; do echo tick$i; sleep 0.3; done" }, TimeoutSeconds = 30 });

            var (result, lines) = await AttachCollectAsync(handle);

            result.Status.ShouldBe(SandboxStatus.Success, "spool advancing within the idle window is never falsely stalled");
            lines.ShouldContain("tick4");
        }
        finally { Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, prior); }
    }

    [Fact]
    public async Task A_durable_run_emitting_newline_less_progress_is_not_stalled()
    {
        // Regression for the review's major finding on the DURABLE path: the watchdog resets on spool BYTE growth, not
        // only on a delivered line — so a run writing a \r-style progress bar with NO newline keeps the file growing
        // and is alive, not falsely stalled. `printf` (no newline) grows out.log every 0.3s inside the 2s window.
        if (OperatingSystem.IsWindows()) return;

        var prior = Environment.GetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, "2");

            var handle = await LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "for i in 1 2 3 4 5 6 7 8 9 10; do printf 'tick'; sleep 0.3; done" }, TimeoutSeconds = 30 });

            var (result, _) = await AttachCollectAsync(handle);

            result.Status.ShouldBe(SandboxStatus.Success, "newline-less byte growth of the spool within the window is never falsely stalled");
        }
        finally { Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, prior); }
    }

    [Theory]
    [InlineData(60, true, SandboxStatus.Success)]
    [InlineData(60, false, SandboxStatus.Stalled)]
    [InlineData(null, true, SandboxStatus.Stalled)]
    public async Task A_silent_durable_run_survives_its_no_progress_window_only_while_a_platform_request_renews_a_lease_a_wall_deadline_backs(int? timeoutSeconds, bool renew, SandboxStatus expected)
    {
        // THE BUG THIS LANE EXISTS TO REMOVE (leg 1): a run parked on an authorised human decision emits nothing, and the
        // only progress signal used to be spool bytes — so the watchdog killed it. Here the run is silent for FIVE
        // no-progress windows while an in-flight platform request renews its lease, and it reaches its own exit code.
        //
        // Leg 2 is the falsifier for the signal: identical run, renewal removed, must still reach Stalled.
        //
        // Leg 3 is the falsifier for the BOUND, and it is the one that keeps this lane honest: TimeoutSeconds null is a
        // supported "no wall-clock" choice, and there this watchdog is the run's ONLY bound — nothing else terminates it
        // and the reconciler cannot collect a run whose observer still heartbeats. So the SAME renewal that rescues leg 1
        // must be refused here, or a wedged unbounded run becomes immortal.
        if (OperatingSystem.IsWindows()) return;

        var prior = Environment.GetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar);
        using var renewing = new CancellationTokenSource();
        try
        {
            Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, "1");

            var handle = await LaunchAsync(ContractSpecs.Sleep(5) with { TimeoutSeconds = timeoutSeconds });
            var leaseDirectory = Path.Combine(handle.SpoolDirectory, "progress");
            handle = handle with { ProgressLeaseDirectory = leaseDirectory };

            if (renew) _ = RenewUntilCancelledAsync(new AgentProgressLease(leaseDirectory), renewing.Token);

            var (result, _) = await AttachCollectAsync(handle);

            result.Status.ShouldBe(expected, timeoutSeconds is null
                ? "with NO wall deadline the watchdog is the only bound, so a renewal must not defer it — otherwise a wedged unbounded run can never be collected"
                : renew
                    ? "a silent run whose platform request keeps renewing the lease is WORKING — killing it is the failure mode this signal removes"
                    : "with the renewal removed the same silent run must still be judged stalled");
        }
        finally
        {
            renewing.Cancel();
            Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, prior);
        }
    }

    /// <summary>Stand in for the host-side platform endpoint holding the run's lease while a tools/call is parked on a human decision — the same <see cref="AgentProgressLease"/> the real <c>ProgressLeaseRenewingHandler</c> writes.</summary>
    private static async Task RenewUntilCancelledAsync(AgentProgressLease lease, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            lease.Renew(AgentProgressSignal.PlatformRequest);

            try { await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    [Fact]
    public async Task Cancelling_the_attach_stops_observing_WITHOUT_killing_the_process()
    {
        if (OperatingSystem.IsWindows()) return;

        // The durability hinge: a backend shutdown cancels the observer, but the supervised run must keep
        // going so a re-attach (after restart) can finish it. A cancel is NOT a kill.
        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(() => AttachCollectAsync(handle, cts.Token));

        File.Exists(Path.Combine(handle.SpoolDirectory, "exit")).ShouldBeFalse("the process has NOT exited — the cancel only stopped observing");
        ProcessIsAlive(handle.ProcessId).ShouldBeTrue("the supervised process survives the observer being torn down");

        KillTree(handle.ProcessId);   // cleanup — the test deliberately left it running
    }

    [Fact]
    public async Task Resuming_from_a_nonzero_StdoutOffset_emits_only_the_lines_after_it()
    {
        if (OperatingSystem.IsWindows()) return;

        // The re-attach foundation: a fresh observer resumes from the dead observer's checkpoint, so the lines
        // already emitted (and already in the append-only event log) are NOT replayed — no duplicate events.
        var handle = await LaunchAsync(ContractSpecs.MultiLine("one", "two", "three"));
        await WaitForExitMarkerAsync(handle);

        var resumed = handle with { StdoutOffset = "one\n".Length };   // 4 bytes — resume past the first line
        var (result, lines) = await AttachCollectAsync(resumed);

        result.Status.ShouldBe(SandboxStatus.Success);
        lines.ShouldBe(new[] { "two", "three" });   // only the lines after the checkpoint offset are re-emitted
    }

    [Fact]
    public async Task Attach_checkpoints_the_advancing_offset_as_it_emits()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.MultiLine("a", "b", "c"));

        var lines = new List<string>();
        var checkpoints = new List<long>();
        await _runner.AttachAsync(handle, (l, _) => { lines.Add(l.Trim()); return Task.CompletedTask; }, default,
            (offset, _) => { checkpoints.Add(offset); return Task.CompletedTask; });

        lines.ShouldBe(new[] { "a", "b", "c" });
        checkpoints.ShouldNotBeEmpty("the observer checkpoints the advancing offset so a re-attach can resume from it");
        checkpoints.ShouldBe(checkpoints.OrderBy(o => o).ToList(), "checkpoints only ever advance");
        checkpoints[^1].ShouldBe("a\nb\nc\n".Length, "the final checkpoint covers every whole line emitted");
    }

    [Fact]
    public async Task A_fresh_attach_to_an_already_exited_run_recovers_its_full_output()
    {
        if (OperatingSystem.IsWindows()) return;

        // Observation is decoupled from launch: this is the foundation the reconciler stands on — a run that
        // finished while no one was watching is still fully recoverable from its spool + exit marker.
        var handle = await LaunchAsync(ContractSpecs.MultiLine("one", "two", "three"));

        await WaitForExitMarkerAsync(handle);

        var (result, lines) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.Success);
        lines.ShouldBe(new[] { "one", "two", "three" });   // attaching after the fact replays the whole spool from offset 0
    }

    [Fact]
    public async Task Probe_reports_exited_with_the_code_once_the_marker_is_present()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.PrintThenExit("x", 3));
        await WaitForExitMarkerAsync(handle);

        var probe = await _runner.ProbeAsync(handle, default);

        probe.State.ShouldBe(SandboxRunState.Exited);
        probe.ExitCode.ShouldBe(3);
    }

    [Fact]
    public async Task Probe_reports_running_while_the_supervised_process_is_alive()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 });

        var probe = await _runner.ProbeAsync(handle, default);

        probe.State.ShouldBe(SandboxRunState.Running);
        probe.ExitCode.ShouldBeNull();

        KillTree(handle.ProcessId);   // cleanup — still running
    }

    [Fact]
    public async Task Probe_reports_gone_when_the_process_died_without_recording_a_marker()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 });

        KillTree(handle.ProcessId);          // killed mid-run → it never writes an exit marker
        await Task.Delay(200);

        var probe = await _runner.ProbeAsync(handle, default);

        probe.State.ShouldBe(SandboxRunState.Gone);
    }

    [Fact]
    public void BuildDurableStartInfo_wraps_the_command_in_a_sh_supervisor_pointing_at_the_spool()
    {
        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", Args = new[] { "--flag", "value" } }, "/tmp/spool-x");

        // On Linux the supervisor is launched under `setsid` (a new session, so it survives a group signal) with
        // /bin/sh as setsid's first arg; on macOS dev there's no setsid binary, so /bin/sh runs directly. Either
        // way the `-c <script> sh <command> <args...>` tail is identical — assert from the shared "-c" anchor.
        if (OperatingSystem.IsLinux())
        {
            info.FileName.ShouldBe("setsid");
            info.ArgumentList[0].ShouldBe("/bin/sh");
        }
        else
        {
            info.FileName.ShouldBe("/bin/sh");
        }

        var c = info.ArgumentList.IndexOf("-c");
        c.ShouldBeGreaterThanOrEqualTo(0, "the supervisor is invoked via sh -c");
        var script = info.ArgumentList[c + 1];
        info.ArgumentList[c + 2].ShouldBe("sh");                       // $0 — the script reads the real command from "$@"
        script.Contains("unset CSP_PID CSP_OUT CSP_ERR CSP_EXIT CSP_LOG_COPY_STATUS", StringComparison.Ordinal).ShouldBeTrue("the child never inherits host spool paths or status-marker authority");
        script.Contains("out_status=$?", StringComparison.Ordinal).ShouldBeTrue("stdout copier success is captured before any host seal can be authorized");
        script.Contains("err_status=$?", StringComparison.Ordinal).ShouldBeTrue("stderr copier success is captured before any host seal can be authorized");
        script.Contains("logs.sealed", StringComparison.Ordinal).ShouldBeFalse("only the host runner, never the child-side script, can mint the final source seal");

        // "$@" is the command, possibly wrapped by prlimit (resource caps, outermost) and/or bwrap (confinement),
        // each terminated by `--`. The REAL command is invariably the trailing tokens, however many wrappers precede.
        var afterDollarZero = info.ArgumentList.Skip(c + 3).ToList();
        // The real command is always the trailing tokens, after any prlimit/bwrap wrappers.
        afterDollarZero.TakeLast(3).ShouldBe(new[] { "mycmd", "--flag", "value" });

        if (BubblewrapSandbox.Available is null && ProcessRlimits.Available is null)
            afterDollarZero.ShouldBe(new[] { "mycmd", "--flag", "value" });   // unconfined + uncapped: command runs directly
        else
            afterDollarZero[0].ShouldNotBe("mycmd", "when this host can sandbox (bwrap) or cap (prlimit), the command is wrapped, not run bare");

        info.Environment["CSP_OUT"].ShouldBe(Path.Combine("/tmp/spool-x", "out.log"));
        info.Environment["CSP_ERR"].ShouldBe(Path.Combine("/tmp/spool-x", "err.log"));
        info.Environment["CSP_EXIT"].ShouldBe(Path.Combine("/tmp/spool-x", "exit"));
        info.Environment["CSP_LOG_COPY_STATUS"].ShouldBe(Path.Combine("/tmp/spool-x", "logs.copy-status"));
        info.Environment["CSP_PID"].ShouldBe(Path.Combine("/tmp/spool-x", "pid"));
    }

    [Fact]
    public void BuildDurableStartInfo_with_resource_caps_disabled_carries_no_prlimit_wrapper()
    {
        // The fix for the live-brain whole-loop false-red: a TRUSTED-fake lane sets MaxProcesses=0 + MaxFileSizeMb=0
        // (via CODESPACE_AGENT_MAX_PROCESSES / _MAX_FILE_MB) so ProcessRlimits.Wrap returns the command UNCHANGED — NO
        // prlimit wrapper. RLIMIT_NPROC is per-UID; on a plain unprivileged shared host it counts the runner's whole
        // process table, so under the supervisor's CONCURRENT multi-agent fan-out a 4096 cap starves the agents' fork()s
        // → signal-kills (Status=Failed) and fork-starved git captures (Succeeded with realPatches=0). Disabling the
        // caps removes that wrapper entirely; this pins that wiring so a future default can't silently re-arm it.
        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", Args = new[] { "--flag", "value" }, MaxProcesses = 0, MaxFileSizeMb = 0 }, "/tmp/spool-caps");

        info.ArgumentList.Any(a => a.Contains("prlimit")).ShouldBeFalse(
            "with both resource caps disabled the durable command must carry NO prlimit wrapper (else a per-UID rlimit can signal-kill or fork-starve a trusted fake on a shared host)");

        // The real command still trails (bwrap may still wrap it when confinement is available; only prlimit is gone).
        info.ArgumentList.TakeLast(3).ShouldBe(new[] { "mycmd", "--flag", "value" });
    }

    [Fact]
    public void BuildDurableStartInfo_runs_the_chain_inside_the_cgroup_self_add_OUTERMOST_before_egress()
    {
        // B4 wiring: the cgroup self-add prefix must wrap the WHOLE chain — outermost, BEFORE the egress netns prefix —
        // so the cgroup.procs write happens on the host before entering the netns and the entire subtree is capped.
        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", Args = new[] { "value" } }, "/tmp/spool-cg",
            egressExecPrefix: new[] { "EGRESS", "y" },
            cgroupExecPrefix: new[] { "CGSELF", "x" });

        var c = info.ArgumentList.IndexOf("-c");
        var afterDollarZero = info.ArgumentList.Skip(c + 3).ToList();   // after `-c <script> sh`

        afterDollarZero.Take(4).ShouldBe(new[] { "CGSELF", "x", "EGRESS", "y" }, "cgroup self-add is OUTERMOST, then the egress netns prefix");
        afterDollarZero.TakeLast(2).ShouldBe(new[] { "mycmd", "value" }, "the real command still trails the wrappers");
    }

    [Fact]
    public void BuildDurableStartInfo_with_no_cgroup_prefix_is_byte_identical_to_before_the_knob()
    {
        // Non-breaking: an absent/empty cgroup prefix (the default — no cap requested / no delegated root) produces the
        // EXACT same argv as a call that never passed the parameter.
        var spec = new SandboxSpec { Command = "mycmd", Args = new[] { "value" } };

        var before = LocalProcessRunner.BuildDurableStartInfo(spec, "/tmp/spool-bi");
        var withEmptyCgroup = LocalProcessRunner.BuildDurableStartInfo(spec, "/tmp/spool-bi", null, Array.Empty<string>());

        withEmptyCgroup.ArgumentList.ShouldBe(before.ArgumentList);
    }


    [Fact]
    public void BuildDurableStartInfo_keeps_the_spool_env_vars_through_a_scrub()
    {
        // The spool paths are added AFTER ApplyEnvironment, so a scrub's Clear() can't drop them — otherwise a
        // scrubbed run would have nowhere to write its output.
        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd" }, "/tmp/spool-y");

        info.Environment.ShouldContainKey("CSP_OUT");
        info.Environment.ShouldContainKey("CSP_ERR");
        info.Environment.ShouldContainKey("CSP_EXIT");
        info.Environment.ShouldContainKey("CSP_LOG_COPY_STATUS");
        info.Environment.ShouldContainKey("CSP_PID");
    }

    [Fact]
    public void BuildDurableStartInfo_keeps_the_non_interactive_defaults_through_the_scrub()
    {
        // C1 is injected at the SHARED ApplyEnvironment choke point, so the durable/bwrap path must carry the
        // non-interactive defaults too (the bwrap argv has no --clearenv → the confined child inherits this env).
        // Guards the durable half of the "one choke point covers both paths" claim against a future reorder of the
        // post-Clear() env assembly — mirrors the spool-env precedent above.
        var info = LocalProcessRunner.BuildDurableStartInfo(new SandboxSpec { Command = "mycmd" }, "/tmp/spool-noninteractive");

        foreach (var (key, value) in NonInteractiveEnv.Defaults)
            info.Environment[key].ShouldBe(value, $"{key} must survive the durable assembly so the bwrap child auto-defaults a prompt");
    }

    [Fact]
    public void BuildDurableStartInfo_points_config_home_env_vars_at_one_fresh_isolated_dir_under_the_spool()
    {
        // A config-isolating harness (Claude Code / Codex) asks for its config-dir var to be redirected so a
        // shelled-out CLI never reads the operator's ~/.claude / ~/.codex. The runner points every requested
        // name at ONE fresh dir under the spool (created here, reaped with the spool dir).
        var spool = Path.Combine(Path.GetTempPath(), "codespace-cfg-" + Guid.NewGuid().ToString("N"));
        _spoolDirs.Add(spool);

        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR", "CODEX_HOME" } }, spool);

        var expected = Path.Combine(spool, "agent-home");
        info.Environment["CLAUDE_CONFIG_DIR"].ShouldBe(expected);
        info.Environment["CODEX_HOME"].ShouldBe(expected, "every requested name points at the single isolated home");
        Directory.Exists(expected).ShouldBeTrue("the home is created so the CLI initializes a clean config there");
    }

    [Fact]
    public void BuildDurableStartInfo_adds_no_config_home_when_a_harness_requests_none()
    {
        var info = LocalProcessRunner.BuildDurableStartInfo(new SandboxSpec { Command = "mycmd" }, "/tmp/spool-noconfig");

        info.Environment.ShouldNotContainKey("CLAUDE_CONFIG_DIR");
        Directory.Exists(Path.Combine("/tmp/spool-noconfig", "agent-home")).ShouldBeFalse("no isolation requested → no per-run config home");
    }

    // ─── MCP wiring: the runner writes the declaration 0600 into config-home + binds the socket (Slice 4) ────

    // The harness renders the Content (FIX 3 — runner writes dumb bytes); here we bake a representative .mcp.json so the
    // write/bind tests have realistic content carrying the socket + token.
    private static McpServerWiring Wiring(string socketPath) => new()
    {
        RelativeFileName = ".mcp.json",
        Content = McpDeclarationWriter.RenderClaudeJson(new McpDeclarationContext { ProxyCommand = "/abs/codespace-mcp", SocketPath = socketPath, Token = "tok-xyz", ServerName = "codespace" }),
        SocketPath = socketPath,
    };

    [Fact]
    public void WriteMcpDeclaration_writes_the_rendered_server_into_the_config_home()
    {
        var configHome = TempDir();

        LocalProcessRunner.WriteMcpDeclaration(Wiring("/tmp/cs/mcp.sock"), configHome);

        var path = Path.Combine(configHome, ".mcp.json");
        File.Exists(path).ShouldBeTrue("the declaration is written at its config-home-relative path");

        var json = File.ReadAllText(path);
        json.ShouldContain("codespace-mcp");
        json.ShouldContain("/tmp/cs/mcp.sock");
        json.ShouldContain("tok-xyz", customMessage: "the run token rides the declaration so the proxy authenticates");
    }

    [Fact]
    public void WriteMcpDeclaration_writes_the_declaration_owner_only_0600()
    {
        if (OperatingSystem.IsWindows()) return;   // unix file modes don't apply

        var configHome = TempDir();

        LocalProcessRunner.WriteMcpDeclaration(Wiring("/tmp/cs/mcp.sock"), configHome);

        // The token lives in this file, so it must NOT be group/other-readable.
        var mode = File.GetUnixFileMode(Path.Combine(configHome, ".mcp.json"));
        mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite, customMessage: "the token-bearing declaration must be 0600");
    }

    [Fact]
    public void WriteMcpDeclaration_is_a_no_op_when_there_is_no_wiring_or_no_config_home()
    {
        var configHome = TempDir();

        // No wiring → nothing written (a run without the tool fabric).
        LocalProcessRunner.WriteMcpDeclaration(null, configHome);
        File.Exists(Path.Combine(configHome, ".mcp.json")).ShouldBeFalse("no wiring → no declaration");

        // No config-home → nowhere harness-isolated to put it → no-op (must not throw).
        Should.NotThrow(() => LocalProcessRunner.WriteMcpDeclaration(Wiring("/tmp/cs/mcp.sock"), null));
    }

    [Fact]
    public void WriteConfigHomeFiles_writes_each_file_at_its_relative_path()
    {
        var configHome = TempDir();

        LocalProcessRunner.WriteConfigHomeFiles(new[]
        {
            new ConfigHomeFile { RelativePath = "skills/tdd/SKILL.md", Content = "tdd body" },
            new ConfigHomeFile { RelativePath = "skills/debug/SKILL.md", Content = "debug body" },
        }, configHome);

        File.ReadAllText(Path.Combine(configHome, "skills", "tdd", "SKILL.md")).ShouldBe("tdd body");
        File.ReadAllText(Path.Combine(configHome, "skills", "debug", "SKILL.md")).ShouldBe("debug body");
    }

    [Fact]
    public void WriteConfigHomeFiles_skips_a_path_that_escapes_the_config_home()
    {
        var configHome = TempDir();

        LocalProcessRunner.WriteConfigHomeFiles(new[] { new ConfigHomeFile { RelativePath = "../escape/SKILL.md", Content = "x" } }, configHome);

        File.Exists(Path.Combine(Path.GetDirectoryName(configHome)!, "escape", "SKILL.md"))
            .ShouldBeFalse("a config-home-escaping relative path is skipped — the runner is the last gate before a write");
    }

    [Fact]
    public void WriteConfigHomeFiles_marks_an_executable_file_executable_and_leaves_others_read_write()
    {
        if (OperatingSystem.IsWindows()) return;

        var configHome = TempDir();

        LocalProcessRunner.WriteConfigHomeFiles(new[]
        {
            new ConfigHomeFile { RelativePath = "hooks/stop-acceptance-check.sh", Content = "#!/bin/sh\nexit 0\n", IsExecutable = true },
            new ConfigHomeFile { RelativePath = "settings.json", Content = "{}" },
        }, configHome);

        var scriptMode = File.GetUnixFileMode(Path.Combine(configHome, "hooks", "stop-acceptance-check.sh"));
        scriptMode.HasFlag(UnixFileMode.UserExecute).ShouldBeTrue(
            "both CLIs invoke the hook by direct command path (\"$CONFIG_DIR\"/hooks/…), which execs the file itself — without +x the shell exits 126 and the hook silently never runs");

        var settingsMode = File.GetUnixFileMode(Path.Combine(configHome, "settings.json"));
        settingsMode.HasFlag(UnixFileMode.UserExecute).ShouldBeFalse("a plain config file stays non-executable — the flag rides per-file, never blanket");
    }

    [Fact]
    public void WriteConfigHomeFiles_is_a_no_op_without_files_or_config_home()
    {
        var configHome = TempDir();

        Should.NotThrow(() => LocalProcessRunner.WriteConfigHomeFiles(Array.Empty<ConfigHomeFile>(), configHome));
        Should.NotThrow(() => LocalProcessRunner.WriteConfigHomeFiles(new[] { new ConfigHomeFile { RelativePath = "skills/x/SKILL.md", Content = "y" } }, null));
    }

    [Fact]
    public void BuildDurableStartInfo_writes_the_mcp_declaration_into_the_config_home_when_wired()
    {
        var spool = Path.Combine(Path.GetTempPath(), "codespace-mcp-decl-" + Guid.NewGuid().ToString("N"));
        _spoolDirs.Add(spool);

        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "claude", ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" }, Mcp = Wiring("/tmp/cs/mcp.sock") }, spool);

        // The declaration lands in the SAME per-run home the config-dir env var points at.
        var home = info.Environment["CLAUDE_CONFIG_DIR"];
        File.Exists(Path.Combine(home, ".mcp.json")).ShouldBeTrue("the runner writes the declaration into the per-run config-home before launch");
    }

    [Fact]
    public void BuildDurableStartInfo_with_no_mcp_wiring_is_byte_identical_to_a_run_without_the_tool_fabric()
    {
        // Flag-OFF byte-identical guarantee: a spec with Mcp=null must produce the EXACT same argv + spool env as the
        // SAME spec built again — no socket bind, no proxy ro-bind, no declaration write. Two builds of the identical
        // Mcp-less spec must match token-for-token (the only source of divergence would be MCP wiring leaking in).
        var spool = Path.Combine(Path.GetTempPath(), "codespace-mcp-off-" + Guid.NewGuid().ToString("N"));
        _spoolDirs.Add(spool);

        SandboxSpec Spec() => new() { Command = "claude", WorkingDirectory = spool, ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" } };

        var a = LocalProcessRunner.BuildDurableStartInfo(Spec(), spool);
        var b = LocalProcessRunner.BuildDurableStartInfo(Spec(), spool);

        a.ArgumentList.ToList().ShouldBe(b.ArgumentList.ToList(), "Mcp=null must add no socket bind / ro-bind — byte-identical argv");

        // And concretely: nothing references the dedicated socket subdir or a proxy bind.
        a.ArgumentList.ShouldNotContain(Path.Combine(spool, "mcp"), customMessage: "Mcp=null must not bind the socket dir");
        File.Exists(Path.Combine(spool, "agent-home", ".mcp.json")).ShouldBeFalse("Mcp=null must write no declaration");
    }

    // ─── B3.2b: the filtered-egress netns prefix wraps the whole supervisor chain ────────────────────────────────

    [Fact]
    public void BuildDurableStartInfo_runs_the_whole_chain_inside_the_egress_netns_prefix_when_one_is_set()
    {
        // When the durable launch sets up a filtered-egress netns it passes the `ip netns exec <ns>` prefix; the
        // supervisor must run the WHOLE chain (prlimit → bwrap → agent) behind it, so its only egress is the netns filter.
        var prefix = new[] { "ip", "netns", "exec", "cs-egr-deadbeef" };

        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", Args = new[] { "--flag", "value" } }, "/tmp/spool-egr", prefix);

        var c = info.ArgumentList.IndexOf("-c");
        info.ArgumentList[c + 2].ShouldBe("sh");                       // $0 — the script reads the real command from "$@"
        var afterDollarZero = info.ArgumentList.Skip(c + 3).ToList();

        afterDollarZero.Take(4).ShouldBe(prefix, "the netns prefix is OUTERMOST — ahead of any prlimit/bwrap wrapper");
        afterDollarZero.TakeLast(3).ShouldBe(new[] { "mycmd", "--flag", "value" }, "the real command still trails every wrapper");

        if (BubblewrapSandbox.Available is not null)
            info.ArgumentList.ShouldNotContain("--unshare-net", customMessage: "inside a filtered netns bwrap SHARES it (inherits the allowlist filter) — it must never --unshare-net the namespace it was placed in");
    }

    [Fact]
    public void BuildDurableStartInfo_without_an_egress_prefix_preserves_today_network_behaviour()
    {
        // No prefix (null) ⇒ no `ip netns exec`, and a network-OFF run still --unshare-nets under bwrap — today's
        // behaviour preserved byte-for-byte. The netns wiring is inert until an enforceable allowlist sets a prefix.
        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", AllowNetwork = false }, "/tmp/spool-noegr");

        info.ArgumentList.ShouldNotContain("netns", customMessage: "no enforceable allowlist ⇒ no netns prefix");

        if (BubblewrapSandbox.Available is not null)
            info.ArgumentList.ShouldContain("--unshare-net", customMessage: "network-off without a netns still severs egress via --unshare-net");
    }

    [Fact]
    public async Task ResolveAllowedIpsBounded_propagates_a_real_cancellation_rather_than_masking_it_as_a_timeout()
    {
        // The bounded resolver converts a TIMEOUT (a black-holed DNS) into a fail-closed setup abort, but a GENUINE run
        // cancellation must propagate as OperationCanceledException (handled as transient by the executor), never be
        // reclassified. A pre-cancelled token + a name (forces the DNS path) exercises the distinction deterministically.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => LocalProcessRunner.ResolveAllowedIpsBoundedAsync(new[] { "example.com" }, cts.Token));
    }

    [Fact]
    public void AppendChildCommand_binds_a_dedicated_socket_dir_NOT_the_spool_dir_so_no_spool_artifacts_leak()
    {
        if (BubblewrapSandbox.Available is null) return;   // bwrap-only: the writable --bind only exists under confinement

        var spool = Path.Combine(Path.GetTempPath(), "codespace-mcp-bind-" + Guid.NewGuid().ToString("N"));
        _spoolDirs.Add(spool);

        // The socket lives in the DEDICATED <spool>/mcp/ subdir (FIX 1) — its parent is that subdir, never the spool dir
        // (which holds out.log/err.log/exit/pid the agent must not read or forge — design §3b / Attack 4).
        var socketPath = Path.Combine(spool, "mcp", "mcp.sock");

        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "claude", WorkingDirectory = spool, ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" }, Mcp = Wiring(socketPath) }, spool);

        var args = info.ArgumentList.ToList();
        var binds = args.Select((a, i) => (a, i)).Where(t => t.a == "--bind").Select(t => args[t.i + 1]).ToList();

        var boundSocketDir = Path.GetDirectoryName(socketPath)!;

        // (a) the socket's dir IS bound writable (so the proxy connects), but it is NOT the spool dir.
        binds.ShouldContain(boundSocketDir, customMessage: "the dedicated MCP socket dir must be bound writable so the proxy can reach it");
        binds.ShouldNotContain(spool, customMessage: "the spool dir itself must NOT be a writable bind — that would expose out.log/err.log/exit/pid to the agent");

        // (b) none of the spool artifacts live under the bound dir.
        foreach (var artifact in new[] { "out.log", "err.log", "exit", "pid" })
            File.Exists(Path.Combine(boundSocketDir, artifact)).ShouldBeFalse($"the bound socket dir must not contain the spool artifact {artifact}");
    }

    [Fact]
    public async Task A_launched_process_sees_its_config_home_env_var_pointing_at_an_isolated_spool_dir()
    {
        if (OperatingSystem.IsWindows()) return;

        // End-to-end on a REAL process: the child a config-isolating harness drives must actually SEE its
        // config-dir var set to a fresh per-run dir under the spool — so Claude Code / Codex read only the
        // config we inject, never the operator's personal ~/.claude / ~/.codex.
        var handle = await LaunchAsync(new SandboxSpec
        {
            Command = "/bin/sh",
            Args = new[] { "-c", "printf '%s' \"$CLAUDE_CONFIG_DIR\"" },
            ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" },
        });

        var (result, lines) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.Success);
        var expected = Path.Combine(handle.SpoolDirectory, "agent-home");
        // The child read CLAUDE_CONFIG_DIR set to the isolated per-run home under the spool (not the operator's ~/.claude).
        lines.ShouldBe(new[] { expected });
        Directory.Exists(expected).ShouldBeTrue("the isolated config home exists for the CLI to initialize into");
    }

    [Theory]
    [InlineData("alpha\nbeta\n", "alpha|beta")]       // trailing newline → the empty remainder is dropped
    [InlineData("alpha\nbeta", "alpha|beta")]         // trailing partial (no newline) → kept
    [InlineData("a\r\nb\r\n", "a|b")]                 // CRLF → the CR is trimmed
    [InlineData("solo", "solo")]
    [InlineData("", "")]
    public void SplitLines_splits_drops_trailing_empty_and_trims_cr(string text, string expectedJoined)
    {
        var expected = expectedJoined.Length == 0 ? Array.Empty<string>() : expectedJoined.Split('|');

        LocalProcessRunner.SplitLines(text).ShouldBe(expected);
    }

    [Fact]
    public void ReadNewLines_emits_whole_lines_holds_a_partial_then_drains_it()
    {
        var path = Path.Combine(TempDir(), "out.log");
        File.WriteAllText(path, "a\nb\npar");

        var (lines, offset) = LocalProcessRunner.ReadNewLines(path, 0, drainPartial: false);
        lines.ShouldBe(new[] { "a", "b" });
        offset.ShouldBe(4, "consumed up to the last newline (\"a\\nb\\n\"); the partial \"par\" is held back");

        var (none, held) = LocalProcessRunner.ReadNewLines(path, offset, drainPartial: false);
        none.ShouldBeEmpty("no further whole line yet");
        held.ShouldBe(offset);

        var (drained, end) = LocalProcessRunner.ReadNewLines(path, offset, drainPartial: true);
        drained.ShouldBe(new[] { "par" });   // the final drain emits the trailing partial
        end.ShouldBe(7);
    }

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("127", true, 127)]
    [InlineData("  42 ", true, 42)]   // surrounding whitespace tolerated
    [InlineData("abc", false, 0)]     // a non-numeric / mid-write marker is "not ready yet"
    public void TryReadExitCode_parses_a_present_numeric_marker(string contents, bool expectedFound, int expectedCode)
    {
        var path = Path.Combine(TempDir(), "exit");
        File.WriteAllText(path, contents);

        LocalProcessRunner.TryReadExitCode(path, out var code).ShouldBe(expectedFound);
        if (expectedFound) code.ShouldBe(expectedCode);
    }

    [Fact]
    public void TryReadExitCode_is_false_when_the_marker_is_absent() =>
        LocalProcessRunner.TryReadExitCode(Path.Combine(TempDir(), "no-such-exit"), out _).ShouldBeFalse();


    [Fact]
    public void Mcp_socket_path_constants_are_pinned()
    {
        // The executor's listener and the runner/proxy connect path agree on these literals — a rename silently breaks the link.
        LocalProcessRunner.McpSocketFile.ShouldBe("mcp.sock");

        // The dedicated socket-only subdir (FIX 1): a rename re-exposes the spool artifacts to the bwrap bind.
        LocalProcessRunner.McpSocketDir.ShouldBe("mcp");

        // The proxy-path override (Rule 8): a rename breaks an operator who pinned a custom codespace-mcp path.
        LocalProcessRunner.McpProxyPathEnvVar.ShouldBe("CODESPACE_MCP_PROXY_PATH");

        // The usable AF_UNIX path maximum: 103 on macOS/BSD, 107 on Linux. The LOWER cap so the short-path fallback
        // fires on every host that would overflow either — a CRITICAL guard against Bind overflowing on this darwin
        // host (empirically .NET binds at length 103 and throws at 104 here).
        LocalProcessRunner.UnixSocketPathCap.ShouldBe(103);
    }

    [Fact]
    public void Mcp_socket_path_is_under_the_spool_dir_for_a_short_root()
    {
        using var settings = RuntimeSettings.Override(s => s with { AgentRunSpoolDirectory = "/tmp/cs" });

        var key = Guid.NewGuid().ToString("N");
        var path = LocalProcessRunner.McpSocketPathFor(key);

        // FIX 1: the socket lives in the DEDICATED <spool>/mcp/ subdir, not directly in the spool dir.
        path.ShouldBe(Path.Combine("/tmp/cs", key, "mcp", "mcp.sock"));
        path.Length.ShouldBeLessThanOrEqualTo(LocalProcessRunner.UnixSocketPathCap);
    }

    [Fact]
    public void Mcp_socket_path_falls_back_to_a_short_unique_path_when_the_canonical_path_overflows_the_cap()
    {
        using var settings = RuntimeSettings.Override(s => s with { AgentRunSpoolDirectory = "/" + new string('x', 120) });

        var key = Guid.NewGuid().ToString("N");
        var path = LocalProcessRunner.McpSocketPathFor(key);

        path.Length.ShouldBeLessThanOrEqualTo(LocalProcessRunner.UnixSocketPathCap, customMessage: "an overflowing canonical path must fall back to a short path that fits the sun_path cap");
        path.ShouldContain(key, customMessage: "the fallback must stay unique per run via the FULL run key (matching the canonical path's uniqueness)");
    }

    [Fact]
    public void Mcp_socket_path_cap_admits_a_bindable_path_and_one_byte_over_overflows()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        // The off-by-one regression test: a path of EXACTLY the admitted cap MUST bind, and one byte over MUST throw.
        // Build the parent dir under temp, then pad the filename so the FULL path hits the exact target length.
        var parent = TempDir();
        var prefix = parent + Path.DirectorySeparatorChar;

        var atCap = prefix + new string('a', LocalProcessRunner.UnixSocketPathCap - prefix.Length);
        atCap.Length.ShouldBe(LocalProcessRunner.UnixSocketPathCap);

        using (var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            Should.NotThrow(() => s.Bind(new UnixDomainSocketEndPoint(atCap)), "a path of exactly UnixSocketPathCap must be bindable");
        }
        try { File.Delete(atCap); } catch { /* best-effort */ }

        // A path well over BOTH platform sun_path ceilings (104 macOS / 108 Linux) MUST be rejected. UnixSocketPathCap+1
        // is NOT a portable overflow probe: it's the macOS usable max + 1, but Linux binds happily up to 107 — so use a
        // generous margin that overflows on every host. The cross-platform guard against the value drifting up is the
        // UnixSocketPathCap.ShouldBe(103) pin; this bind check proves the host actually rejects an over-length path.
        const int clearlyOverAnyCap = 130;

        var overCap = prefix + new string('a', clearlyOverAnyCap - prefix.Length);
        overCap.Length.ShouldBe(clearlyOverAnyCap);

        using var over = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Should.Throw<Exception>(() =>
        {
            var ep = new UnixDomainSocketEndPoint(overCap);   // the UDS endpoint ctor (or Bind) rejects an over-length sun_path
            over.Bind(ep);
        }).ShouldBeAssignableTo<ArgumentException>("a path well over the AF_UNIX sun_path cap must overflow on every host");
    }

    [Fact]
    public async Task Mcp_socket_path_fallback_is_genuinely_bindable_and_round_trips_a_byte()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        // The fallback branch (sun_path overflow) must yield a path that BINDS, not merely a short string. Force the
        // fallback with a long spool root, then bind a real listener, connect a client, and round-trip one byte.
        using var settings = RuntimeSettings.Override(s => s with { AgentRunSpoolDirectory = "/" + new string('x', 120) });

        var key = Guid.NewGuid().ToString("N");
        var path = LocalProcessRunner.McpSocketPathFor(key);
        path.Length.ShouldBeLessThanOrEqualTo(LocalProcessRunner.UnixSocketPathCap, "the fallback must fit the sun_path cap");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(backlog: 1);

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(path));

            using var server = await listener.AcceptAsync();

            await client.SendAsync(new byte[] { 0x42 }, SocketFlags.None);
            var buf = new byte[1];
            var n = await server.ReceiveAsync(buf, SocketFlags.None);

            n.ShouldBe(1, "the fallback socket carried the byte");
            buf[0].ShouldBe((byte)0x42, "the byte round-tripped over the genuinely-bound fallback socket");
        }
        finally { try { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { /* best-effort */ } }
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs-spool-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _spoolDirs.Add(dir);
        return dir;
    }

    private async Task WaitForExitMarkerAsync(SandboxHandle handle)
    {
        var marker = Path.Combine(handle.SpoolDirectory, "exit");
        for (var i = 0; i < 100 && !File.Exists(marker); i++) await Task.Delay(50);
        File.Exists(marker).ShouldBeTrue("the quick command should have finished + recorded its exit marker within ~5s");
    }

    private static bool ProcessIsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static void KillTree(int pid)
    {
        try { using var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        foreach (var dir in _spoolDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
