using System.Text;
using CodeSpace.Core.Services.Workflows.Artifacts;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The run's FAITHFUL raw transcript, accumulated without retaining it — the sibling of
/// <see cref="AgentResultFold"/> for the other half of what the executor held per streaming run.
///
/// <para><b>Why it exists.</b> <see cref="AgentResultFold"/> bounded the run's parsed events; the transcript
/// <c>StringBuilder</c> beside it still grew with the whole stdout, and it is a byte-SUPERSET of those event texts
/// (it keeps the lines <c>ParseEvents</c> dropped), so it was the larger of the two. Held as UTF-16 it costs ~2× the
/// stream's bytes for the whole run, and completing it cost another ~2× at <c>ToString()</c> plus ~1× at the UTF-8
/// encode — so a long agent exhausted the heap and the executor's generic catch landed a SUCCEEDED agent as
/// Failed("executor-error").</para>
///
/// <para><b>What it retains.</b> At most <see cref="ArtifactStoreConfig.InlineThresholdBytes"/> — deliberately the
/// SAME budget the artifact offloader uses to decide inline-vs-offload, not an independent cap. Content at or under
/// it is exactly the content that must stay inline on the durable result, so it is held as the original text and the
/// inline string is byte-identical to the old builder's. The first byte PAST it is content the offloader was always
/// going to move to the artifact store, so it can leave the heap the moment it crosses: the retained head is
/// encoded into a spill file and every later line is written straight there. Spilling therefore cannot change the
/// inline-vs-offload decision for ANY threshold an operator configures — the two decisions are the same comparison.</para>
///
/// <para><b>Byte-identity.</b> Every append is a whole line plus <see cref="System.Environment.NewLine"/>, exactly
/// as <c>StringBuilder.AppendLine</c> produced, and UTF-8 encoding is per-code-point, so encoding the pieces and
/// concatenating them equals encoding the concatenation — the split always falls after an ASCII newline, never
/// inside a surrogate pair. <see cref="MarkSeam"/> reproduces the revise-round join, including its two empty cases:
/// the seam is emitted lazily before the next line and only when content already exists, so an empty round
/// contributes no seam. <c>AgentTranscriptSpoolTests</c> pins all of this differentially against a frozen
/// transcription of the pre-change builder + join.</para>
///
/// <para><b>Completion handoff.</b> A spilled spool is sealed before artifact placement: its writer is flushed and
/// closed, mutation stops, and <see cref="IArtifactWriteSource.OpenReadAsync"/> returns a fresh file handle for the
/// store's identity pass and every placement retry. The executor therefore never calls <see cref="ReadAllAsync"/>
/// for spilled content. That whole-byte method remains only as a compatibility/test read, while production completion
/// stays O(inline threshold + fixed copy buffers) instead of allocating O(transcript length).</para>
///
/// <para><b>Where the spill lives.</b> Inside the RUN'S OWN spool directory (<c>AgentRunExecutor.TranscriptSpillDirectory</c>),
/// never the system temp directory — and that placement is load-bearing twice over. It is the operator's configured
/// <c>Agents:RunSpoolDirectory</c> volume, the same disk the runner already copies this run's raw stdout to and the one
/// a Production host is REFUSED unless it points off temp (<see cref="CodeSpace.Core.Settings.DurableRootsGuard"/>);
/// and it is inside the directory <see cref="AgentRunSpoolReaper"/> already deletes recursively once the run is
/// terminal and past its retention window. So dispose removes the file on the clean unwind, and the reaper is the
/// backstop for every exit that is not one: a worker killed by the heap exhaustion this class exists to bound, by a
/// pod eviction or by a deploy leaves that full copy under the SAME retention window as the rest of its spool
/// instead of on the host forever. That backstop rides on the run carrying a runner handle (the reaper's candidate
/// set), which every production run does — <c>LocalProcessRunner</c> is durable. The file name is per-instance random
/// so a re-attaching worker can neither collide with nor clobber a dead worker's leftover; the run linkage is the
/// directory, and the reaper's recursive delete claims both files.</para>
///
/// <para>Single-threaded by contract: one spool belongs to one run's line-by-line accumulation (the executor's
/// PersistLineAsync), mirroring the sequential fold and event writer next to it.</para>
/// </summary>
public sealed class AgentTranscriptSpool : IArtifactWriteSource, IAsyncDisposable
{
    private const int SpillBufferBytes = 64 * 1024;

    private readonly StringBuilder _retained = new();
    private readonly string _spillDirectory;
    private readonly int _budgetBytes;
    private long _lengthBytes;
    private string? _pendingSeam;
    private string? _spillPath;
    private FileStream? _spill;
    private bool _spilled;
    private bool _sealed;
    private bool _disposed;
    private int _openReadCount;

    public AgentTranscriptSpool(string spillDirectory) : this(spillDirectory, ArtifactStoreConfig.InlineThresholdBytes) { }

    /// <summary>Test seam: the retained budget in UTF-8 bytes. Production always uses <see cref="ArtifactStoreConfig.InlineThresholdBytes"/>, so the spill boundary and the offload boundary are the same comparison.</summary>
    internal AgentTranscriptSpool(string spillDirectory, int budgetBytes)
    {
        _spillDirectory = spillDirectory;
        _budgetBytes = budgetBytes;
    }

    /// <summary>Whether the content outgrew the retained budget and now lives in the spill file — equivalently, whether the artifact offloader would have moved it out of the result row. STICKY, deliberately not derived from the live handle: dispose closes that handle, and a <c>Spilled</c> that flipped back to false there would turn <see cref="RetainedText"/>'s refusal into a silent read of the already-cleared buffer.</summary>
    public bool Spilled => _spilled;

    /// <summary>The whole transcript's UTF-8 length, retained or spilled.</summary>
    public long LengthBytes => _lengthBytes;

    /// <summary>The retained buffer's ACTUAL occupancy in chars — bounded by the budget whatever the stream's size, since a UTF-8 byte count is never below its char count. The invariant the memory-shape test measures.</summary>
    internal int RetainedCharCount => _retained.Length;

    /// <summary>The spill file backing this transcript, or null while it is still retained — so a test can prove dispose removes it.</summary>
    internal string? SpillPath => _spillPath;

    /// <summary>Test-visible count of fresh sealed read handles. Production correctness does not depend on it.</summary>
    internal int OpenReadCount => _openReadCount;

    /// <summary>The transcript as text, valid only while it fits the retained budget AND the spool is still alive. Byte-identical to what the pre-change <c>StringBuilder.ToString()</c> produced.</summary>
    public string RetainedText()
    {
        EnsureNotDisposed();

        if (_spilled)
            throw new InvalidOperationException($"The transcript spilled at {_lengthBytes} bytes and is no longer retained; read it with {nameof(ReadAllAsync)}.");

        return _retained.ToString();
    }

    /// <summary>Append one already-redacted raw line, terminated exactly as <c>StringBuilder.AppendLine</c> terminated it. Emits a pending revise seam first when there is already content for it to separate.</summary>
    public async ValueTask AppendLineAsync(string line, CancellationToken cancellationToken)
    {
        EnsureWritable();
        if (_pendingSeam is { } seam)
        {
            _pendingSeam = null;

            if (_lengthBytes > 0) await WriteAsync(seam, cancellationToken).ConfigureAwait(false);
        }

        await WriteAsync(line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mark a round boundary: <paramref name="seam"/> separates what is already here from the next line appended.
    /// Deferred rather than written now, because the pre-change join emitted NO seam for a round that produced
    /// nothing — neither before it (nothing yet to separate) nor after it (nothing arrived to separate from).
    /// </summary>
    public void MarkSeam(string seam)
    {
        EnsureWritable();
        _pendingSeam = seam;
    }

    /// <summary>
    /// Flush and close the spill writer, freezing the exact byte range that artifact identity admission may reopen.
    /// Only spilled spools are stream sources; retained content continues through <see cref="RetainedText"/>.
    /// </summary>
    public async Task SealAsync(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        if (!_spilled) throw new InvalidOperationException("Only a spilled transcript can be sealed as an artifact stream source.");
        if (_sealed) return;
        cancellationToken.ThrowIfCancellationRequested();

        var spill = _spill ?? throw new InvalidOperationException("The spilled transcript has no writable file to seal.");
        await spill.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (spill.Length != _lengthBytes)
            throw new InvalidDataException($"Transcript spill length mismatch before seal: expected {_lengthBytes} bytes, observed {spill.Length}.");
        await spill.DisposeAsync().ConfigureAwait(false);
        _spill = null;
        _sealed = true;
    }

    /// <summary>Open a new read handle over immutable spilled bytes. Refuses live/unsealed and retained spools.</summary>
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        if (!_spilled) throw new InvalidOperationException("A retained transcript is not an artifact stream source.");
        if (!_sealed) throw new InvalidOperationException("The spilled transcript must be sealed before it can be opened for artifact storage.");
        cancellationToken.ThrowIfCancellationRequested();

        var path = _spillPath ?? throw new InvalidOperationException("The sealed transcript has no spill path.");
        var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, SpillBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var observedLength = content.Length;
        if (observedLength != _lengthBytes)
        {
            content.Dispose();
            throw new InvalidDataException($"Transcript spill length changed after seal: expected {_lengthBytes} bytes, observed {observedLength}.");
        }

        _openReadCount++;
        return ValueTask.FromResult<Stream>(content);
    }

    /// <summary>The whole transcript's UTF-8 bytes for compatibility/tests. Production completion streams a sealed spill through <see cref="IArtifactWriteSource"/> and does not call this method.</summary>
    public async Task<byte[]> ReadAllAsync(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();

        if (!_spilled) return Encoding.UTF8.GetBytes(_retained.ToString());

        if (_sealed)
        {
            await using var content = await OpenReadAsync(cancellationToken).ConfigureAwait(false);
            var sealedBytes = new byte[checked((int)_lengthBytes)];
            await content.ReadExactlyAsync(sealedBytes, cancellationToken).ConfigureAwait(false);
            return sealedBytes;
        }

        await _spill!.FlushAsync(cancellationToken).ConfigureAwait(false);
        _spill.Seek(0, SeekOrigin.Begin);

        var bytes = new byte[_spill.Length];
        await _spill.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        if (_spill is not null) await _spill.DisposeAsync().ConfigureAwait(false);

        _spill = null;

        if (_spillPath is null) return;

        try { File.Delete(_spillPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        _spillPath = null;
    }

    /// <summary>Append raw text: retained while it still fits the budget, spilled the moment it does not.</summary>
    private async ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        if (text.Length == 0) return;

        _lengthBytes += Encoding.UTF8.GetByteCount(text);

        if (_spill is null && _lengthBytes <= _budgetBytes)
        {
            _retained.Append(text);
            return;
        }

        if (_spill is null) await SpillAsync(cancellationToken).ConfigureAwait(false);

        await _spill!.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Move the retained head (bounded by the budget) into a fresh spill file under the run's own spool directory and stop retaining.</summary>
    private async Task SpillAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_spillDirectory);

        _spillPath = Path.Combine(_spillDirectory, $"transcript-{Guid.NewGuid():N}.spill");
        _spill = new FileStream(_spillPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, SpillBufferBytes, useAsync: true);
        _spilled = true;

        var head = Encoding.UTF8.GetBytes(_retained.ToString());
        _retained.Clear();

        await _spill.WriteAsync(head, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads after dispose fail LOUD. The spill file is deleted and the retained buffer was cleared into it, so a permissive read hands the caller <c>""</c> and the run persists a silently EMPTY durable transcript with no artifact ref — data loss with no signal. Enforced by the type, not by the lexical ordering inside the executor's two long methods.</summary>
    private void EnsureNotDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AgentTranscriptSpool), $"The transcript spool was disposed; attach/read it with {nameof(RetainedText)}/{nameof(SealAsync)}/{nameof(OpenReadAsync)} BEFORE it leaves scope — afterwards its spill file is gone and only a falsely empty transcript is left to return.");
    }

    private void EnsureWritable()
    {
        EnsureNotDisposed();
        if (_sealed) throw new InvalidOperationException("The transcript spool is sealed and can no longer accept lines or revise seams.");
    }
}
