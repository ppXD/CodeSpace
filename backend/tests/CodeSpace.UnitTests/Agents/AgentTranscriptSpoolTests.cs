using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Artifacts;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// G0: the transcript accumulator that replaced the executor's whole-run <c>StringBuilder</c>. Two properties are
/// under test and they pull against each other, which is why this file exists rather than a smoke test:
///
/// <para><b>Byte-identity.</b> The transcript is the durable "replay the exact session" record — inline on
/// <c>result_jsonb</c> when small, an artifact's bytes when large — so the spool must produce the SAME bytes the
/// pre-change builder + <c>JoinTranscripts</c> produced, for every stream shape and on both sides of the spill
/// boundary. <see cref="FrozenTranscript"/> is a transcription of that shipped code, kept here so the comparison is
/// against what actually ran rather than a re-derivation of it.</para>
///
/// <para><b>Bounded retention.</b> What the spool holds must not grow with the stream, which is the whole point:
/// the unbounded builder is what landed a succeeded agent as Failed("executor-error").</para>
///
/// <para><b>Containment.</b> Trading heap for disk only helps if the disk is reclaimable, so where the spill lands and
/// how a read of a dead spool behaves are pinned too: the file must sit where <see cref="AgentRunSpoolReaper"/> already
/// sweeps (a worker killed by the very OOM this bounds never reaches the dispose that deletes it), and a read after
/// dispose must fail LOUD rather than hand back the cleared buffer as an empty transcript.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentTranscriptSpoolTests : IDisposable
{
    /// <summary>The revise-round seam, read from production so the test cannot drift from the separator the join actually emitted.</summary>
    private const string Seam = AgentRunExecutor.ReviseTranscriptSeam;

    private readonly List<string> _spillDirectories = new();

    /// <summary>
    /// A representative multi-round stream: a redaction hit (the executor redacts before it appends, so the
    /// placeholder is what lands), CJK, emoji outside the BMP (a surrogate pair — the case where encoding pieces
    /// separately could have diverged from encoding the whole), a whitespace-only line <c>ParseEvents</c> drops but
    /// the transcript keeps, an EMPTY revise round (which must contribute no seam), and a final line the runner
    /// delivered without a trailing newline (a drained partial, terminated by the transcript exactly as every other
    /// line is).
    /// </summary>
    private static readonly string[][] MultiRoundStream =
    {
        new[] { "{\"type\":\"system\",\"key\":\"***\"}", "   ", "读取檔案 backend/src/資料.cs", "done 🚀🎉 shipped" },
        new[] { "REVISE: retrying", "", "修正完成 ✅" },
        Array.Empty<string>(),
        new[] { "final partial line with no newline of its own" },
    };

    // ─── Byte-identity against the frozen pre-change accumulation ─────────────

    [Theory]
    [InlineData(1_000_000)]   // never spills: the whole stream is retained and read back from memory
    [InlineData(16)]          // spills on the first line: almost everything is read back from the file
    [InlineData(64)]          // spills mid-stream: the read spans the retained head AND the spilled tail
    public async Task Produces_the_same_bytes_the_frozen_accumulation_produced(int budgetBytes)
    {
        var spooled = await SpoolBytesAsync(budgetBytes, MultiRoundStream);

        spooled.ShouldBe(Encoding.UTF8.GetBytes(FrozenTranscript(MultiRoundStream)!),
            "the transcript is a durable record read byte-for-byte from the artifact store — spilling may not alter one byte of it, at any budget");
    }

    [Theory]
    [InlineData(1_000_000)]
    [InlineData(16)]
    public async Task An_empty_round_contributes_no_seam_wherever_it_falls(int budgetBytes)
    {
        // The pre-change join short-circuited BOTH empty cases: an empty first round left the second round's
        // transcript bare, and an empty later round left no seam behind it. A spool that wrote the seam eagerly at
        // the round boundary would insert one in both places.
        string[][] emptyFirst = { Array.Empty<string>(), new[] { "second round only" } };
        string[][] emptyMiddle = { new[] { "first" }, Array.Empty<string>(), new[] { "third" } };

        (await SpoolBytesAsync(budgetBytes, emptyFirst)).ShouldBe(Encoding.UTF8.GetBytes(FrozenTranscript(emptyFirst)!),
            "an empty first round leaves nothing for a seam to separate");
        (await SpoolBytesAsync(budgetBytes, emptyMiddle)).ShouldBe(Encoding.UTF8.GetBytes(FrozenTranscript(emptyMiddle)!),
            "an empty middle round is joined over, not seamed twice");

        Encoding.UTF8.GetString(await SpoolBytesAsync(budgetBytes, emptyMiddle)).Split(Seam).Length
            .ShouldBe(2, "three rounds with a silent middle one produce exactly ONE seam");
    }

    [Fact]
    public async Task A_run_that_never_emitted_a_line_stays_the_empty_transcript()
    {
        await using var spool = new AgentTranscriptSpool(NewSpillDirectory(), 16);

        spool.MarkSeam(Seam);

        spool.Spilled.ShouldBeFalse();
        spool.LengthBytes.ShouldBe(0);
        spool.RetainedText().ShouldBe("", "a marked-but-never-written seam must not invent content for a silent run");
    }

    // ─── The spill boundary IS the offload boundary ────────────────────────────

    [Fact]
    public async Task Content_that_still_fits_the_budget_is_retained_and_the_first_byte_past_it_spills()
    {
        var lineBytes = "abcd".Length + Environment.NewLine.Length;

        await using var fits = new AgentTranscriptSpool(NewSpillDirectory(), lineBytes);
        await fits.AppendLineAsync("abcd", CancellationToken.None);

        await using var over = new AgentTranscriptSpool(NewSpillDirectory(), lineBytes - 1);
        await over.AppendLineAsync("abcd", CancellationToken.None);

        // The offloader keeps content INLINE when its UTF-8 length is <= the threshold and moves it out above that.
        // The spool's budget is that same threshold, so these two assertions are what make spilling invisible to the
        // durable record: exactly the content that would have stayed inline is the content still held as text.
        fits.Spilled.ShouldBeFalse("content whose length equals the threshold stays inline on the result, so it stays retained");
        fits.RetainedText().ShouldBe("abcd" + Environment.NewLine);
        over.Spilled.ShouldBeTrue("one byte past the threshold is content the offloader was always going to move to the artifact store");
        over.LengthBytes.ShouldBe(lineBytes, "spilling does not change how much transcript there is");
    }

    // ─── Bounded retention ────────────────────────────────────────────────────

    [Fact]
    public async Task Retention_stops_growing_once_the_stream_passes_the_budget()
    {
        const int budgetBytes = 64;
        const int lines = 20_000;

        await using var spool = new AgentTranscriptSpool(NewSpillDirectory(), budgetBytes);

        for (var i = 0; i < lines; i++)
            await spool.AppendLineAsync($"transcript line {i:D6} padding-padding-padding", CancellationToken.None);

        spool.LengthBytes.ShouldBeGreaterThan(800_000, "the stream itself is ~0.8 MB — the test is only meaningful if the content really is far past the budget");
        spool.RetainedCharCount.ShouldBeLessThanOrEqualTo(budgetBytes,
            "the retained buffer must not grow with the stream — an O(stdout) transcript is what exhausted the heap and landed a succeeded agent as Failed(\"executor-error\")");

        // Retention being bounded must not have cost fidelity: the whole stream is still there, head and tail.
        var recovered = Encoding.UTF8.GetString(await spool.ReadAllAsync(CancellationToken.None));
        recovered.ShouldContain("transcript line 000000 padding");
        recovered.ShouldContain($"transcript line {lines - 1:D6} padding");
    }

    [Fact]
    public async Task A_spilled_source_flushes_and_seals_before_each_open_replays_the_exact_same_bytes()
    {
        await using var spool = new AgentTranscriptSpool(NewSpillDirectory(), 16);
        foreach (var round in MultiRoundStream)
        {
            if (spool.LengthBytes > 0) spool.MarkSeam(Seam);
            foreach (var line in round) await spool.AppendLineAsync(line, CancellationToken.None);
        }
        var expected = Encoding.UTF8.GetBytes(FrozenTranscript(MultiRoundStream)!);
        var source = (IArtifactWriteSource)spool;

        await Should.ThrowAsync<InvalidOperationException>(async () => await source.OpenReadAsync(CancellationToken.None),
            "opening before seal could expose buffered or still-mutating bytes to identity admission");

        await spool.SealAsync(CancellationToken.None);
        var first = await ReadSourceAsync(source);
        var second = await ReadSourceAsync(source);

        first.ShouldBe(expected);
        second.ShouldBe(expected, "storage identity admission and placement each receive a fresh stream over identical sealed bytes");
        source.LengthBytes.ShouldBe(expected.LongLength);
        spool.OpenReadCount.ShouldBe(2);
        await Should.ThrowAsync<InvalidOperationException>(() => spool.AppendLineAsync("too late", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task A_cancelled_seal_does_not_claim_the_source_is_stable_or_prevent_a_later_clean_seal()
    {
        await using var spool = new AgentTranscriptSpool(NewSpillDirectory(), 8);
        await spool.AppendLineAsync("long enough to spill", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => spool.SealAsync(cancellation.Token));
        await Should.ThrowAsync<InvalidOperationException>(async () => await ((IArtifactWriteSource)spool).OpenReadAsync(CancellationToken.None));

        await spool.AppendLineAsync("still writable after refused seal", CancellationToken.None);
        await spool.SealAsync(CancellationToken.None);
        Encoding.UTF8.GetString(await ReadSourceAsync(spool)).ShouldEndWith($"still writable after refused seal{Environment.NewLine}");
    }

    // ─── Containment: reclaimable on disk, loud when dead ─────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposing_a_live_or_sealed_spilled_transcript_removes_its_file(bool isSealed)
    {
        await using var spool = new AgentTranscriptSpool(NewSpillDirectory(), 16);
        await spool.AppendLineAsync("long enough to spill this transcript", CancellationToken.None);
        if (isSealed) await spool.SealAsync(CancellationToken.None);

        var path = spool.SpillPath.ShouldNotBeNull();
        File.Exists(path).ShouldBeTrue();

        await spool.DisposeAsync();

        File.Exists(path).ShouldBeFalse("a completed run must not leave its transcript on the worker's disk");
    }

    [Fact]
    public async Task A_spilled_transcript_lands_where_the_run_spool_reaper_will_claim_it()
    {
        var runId = Guid.NewGuid();
        var directory = TrackSpillDirectory(AgentRunExecutor.TranscriptSpillDirectory(runId));

        await using var spool = new AgentTranscriptSpool(directory, 16);
        await spool.AppendLineAsync("long enough to spill this transcript", CancellationToken.None);

        var spilledIn = Path.GetDirectoryName(spool.SpillPath.ShouldNotBeNull()).ShouldNotBeNull();

        // Dispose is the HAPPY path only. A worker killed by the heap exhaustion this spool exists to bound, by a pod
        // eviction or by a deploy never reaches it, so the reaper is the only thing between a spilled transcript and a
        // permanent full copy of that run's output on the host. It sweeps the run's spool FAMILY and refuses any path
        // not strictly under the spool root, so a temp-rooted spill is neither enumerated nor admissible — and is off
        // the operator's configured spool volume, which a Production host is refused a temp-rooted one of.
        AgentRunSpoolReaper.IsUnderSpoolRoot(spilledIn).ShouldBeTrue("a spill outside the spool root is inadmissible to the reaper's containment guard, so nothing in the system can ever reclaim it");
        AgentRunSpoolReaper.RoundSpoolFamily(runId).ShouldContain(spilledIn, "the reaper deletes the run's spool family recursively; a spill anywhere else has no retention window at all");
    }

    [Fact]
    public async Task Reading_a_spilled_transcript_as_text_is_refused_rather_than_silently_empty()
    {
        await using var spool = new AgentTranscriptSpool(NewSpillDirectory(), 16);
        await spool.AppendLineAsync("long enough to spill this transcript", CancellationToken.None);

        Should.Throw<InvalidOperationException>(() => spool.RetainedText())
            .Message.ShouldContain("spilled", customMessage: "a caller that inlines a spilled transcript would persist a TRUNCATED durable record; it must not compile-silently return the empty head");
    }

    [Theory]
    [InlineData(true)]    // spilled: the retained buffer was already CLEARED into the file, so a permissive read hands back ""
    [InlineData(false)]   // retained: loud for every shape, so the invariant holds whatever the run's size turned out to be
    public async Task Reading_a_disposed_transcript_is_refused_rather_than_silently_empty(bool spilled)
    {
        await using var spool = new AgentTranscriptSpool(NewSpillDirectory(), spilled ? 16 : 1_000_000);
        await spool.AppendLineAsync("a line the durable record must not lose", CancellationToken.None);

        await spool.DisposeAsync();

        // Today AttachTranscriptAsync happens to precede both disposals, but that is enforced only by lexical ordering
        // inside two long methods, and getting it wrong persists Transcript="" with NO artifact ref — a silently empty
        // durable transcript rather than an exception. So the refusal must survive dispose instead of failing open there.
        spool.Spilled.ShouldBe(spilled, "the spilled state is a fact about the record's shape, not about the live file handle — dispose must not flip it");
        Should.Throw<ObjectDisposedException>(() => spool.RetainedText())
            .Message.ShouldContain("disposed", customMessage: "the guard whose whole purpose is to stop a caller persisting a TRUNCATED transcript must not stop guarding at the moment it would go silent");
        await Should.ThrowAsync<ObjectDisposedException>(() => spool.ReadAllAsync(CancellationToken.None));
        if (spilled)
            await Should.ThrowAsync<ObjectDisposedException>(async () => await ((IArtifactWriteSource)spool).OpenReadAsync(CancellationToken.None));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>A fresh run's spill directory, derived exactly as production derives it — so what these tests spill lands where production spills it, under the reaper's root rather than in the temp dir.</summary>
    private string NewSpillDirectory() => TrackSpillDirectory(AgentRunExecutor.TranscriptSpillDirectory(Guid.NewGuid()));

    /// <summary>Remember a spill directory for teardown: it is real, on the operator's spool volume, so a suite run must not accumulate one per spilling test.</summary>
    private string TrackSpillDirectory(string directory)
    {
        _spillDirectories.Add(directory);

        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _spillDirectories)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<byte[]> SpoolBytesAsync(int budgetBytes, IReadOnlyList<IReadOnlyList<string>> rounds)
    {
        await using var spool = new AgentTranscriptSpool(NewSpillDirectory(), budgetBytes);

        for (var round = 0; round < rounds.Count; round++)
        {
            if (round > 0) spool.MarkSeam(Seam);

            foreach (var line in rounds[round]) await spool.AppendLineAsync(line, CancellationToken.None);
        }

        return await spool.ReadAllAsync(CancellationToken.None);
    }

    private static async Task<byte[]> ReadSourceAsync(IArtifactWriteSource source)
    {
        await using var content = await source.OpenReadAsync(CancellationToken.None);
        using var output = new MemoryStream();
        await content.CopyToAsync(output);
        return output.ToArray();
    }

    /// <summary>
    /// FROZEN transcription of the pre-change accumulation: one <c>StringBuilder</c> per round with
    /// <c>AppendLine</c> per already-redacted line, folded through the <c>AgentRunExecutor.JoinTranscripts</c> this
    /// change removed, exactly as the revise loop folded it (prior = everything joined so far, current = the round
    /// that just finished). A literal copy, so the differential compares the spool against what actually shipped —
    /// every offloaded transcript already in the artifact store was produced by the code below.
    /// </summary>
    private static string? FrozenTranscript(IReadOnlyList<IReadOnlyList<string>> rounds)
    {
        string? joined = null;

        foreach (var round in rounds)
        {
            var builder = new StringBuilder();

            foreach (var line in round) builder.AppendLine(line);

            joined = FrozenJoin(joined, builder.ToString());
        }

        return joined;
    }

    private static string? FrozenJoin(string? prior, string? current) =>
        string.IsNullOrEmpty(prior) ? current
        : string.IsNullOrEmpty(current) ? prior
        : $"{prior}\n--- revise round ---\n{current}";
}
