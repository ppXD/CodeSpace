using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;

/// <summary>
/// The rules every lane applies to a judge's scope once its bytes have been put back from the platform: which files a
/// runtime loads purely by being present (and so are never left as the candidate wrote them), and what a grade concludes
/// when the scope changed while its check ran.
///
/// <para><b>Why a comparison and not a lock.</b> The check runs as the user that owns the restored files, so the read-only
/// seal (<see cref="OracleTree.Seal"/>) is only a speed bump: a subject the judge runs can put a write bit back, or unlink
/// and recreate a file in a writable directory. Fingerprinting the scope before and after the check catches either way.
/// A pass decided against platform-owned bytes that changed under it is VOIDED; any other movement in the judge's
/// directory is labelled, so the pass can never read as clean.</para>
/// </summary>
public static class OracleGuard
{
    /// <summary>The prefix every voided-tamper note starts with. Pinned by test (Rule 8): the Room, the metrics that count flagged cells and the tests key on the literal.</summary>
    public const string TamperNoteMarker = "ORACLE TAMPER VOIDED";

    /// <summary>The detail a PASS becomes when its judge's platform-owned bytes changed while the check ran. Genuine, never infra: the candidate's own code did it. Pinned by test (Rule 8).</summary>
    public const string ChangedDuringCheckDetail = "tests-judge-changed-during-check";

    private const int NoteMaxChars = 200;

    /// <summary>Whether <paramref name="oracleNote"/> reports a voided tamper — the work touched its judge, before or during the check.</summary>
    public static bool IsTamperFlagged(string? oracleNote) => oracleNote?.Contains(TamperNoteMarker, StringComparison.Ordinal) == true;

    /// <summary>
    /// Whether a runtime loads <paramref name="path"/> just because it is there: pytest's <c>conftest.py</c>, the site
    /// module's <c>sitecustomize.py</c>/<c>usercustomize.py</c>, and <c>*.pth</c> files. A judge never names these, so a
    /// candidate's copy beside a judge steers it without appearing in anything the judge reads on purpose.
    /// </summary>
    public static bool IsAutoLoaded(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];

        return name is "conftest.py" or "sitecustomize.py" or "usercustomize.py" || name.EndsWith(".pth", StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="grade"/> as it stands once the check is over, given its scopes' fingerprints from just before and
    /// just after the check and which paths were platform-owned when it started. Unchanged when nothing moved.
    /// </summary>
    public static BenchmarkGrade AfterCheck(BenchmarkGrade grade, OracleTree.ScopeFingerprint before, OracleTree.ScopeFingerprint after, Func<string, bool> isPlatformOwned)
    {
        if (!before.Complete || !after.Complete) return Label(grade, "its judge's directory is too large to compare before and after the check");

        var comparison = before.Compare(after);
        var voided = comparison.Changed.Where(isPlatformOwned).ToList();
        var moved = comparison.Changed.Except(voided).Concat(comparison.Added).OrderBy(path => path, StringComparer.Ordinal).ToList();

        if (voided.Count > 0) grade = Void(grade, voided);

        return moved.Count == 0 ? grade : Label(grade, $"the check changed its judge's directory while it ran: {Flatten(moved)}");
    }

    /// <summary><paramref name="paths"/> as ONE bounded line — a note rides a detail string, never a paragraph.</summary>
    public static string Flatten(IEnumerable<string> paths)
    {
        var joined = string.Join(", ", paths);

        return joined.Length <= NoteMaxChars ? joined : joined[..NoteMaxChars] + "…";
    }

    private static BenchmarkGrade Void(BenchmarkGrade grade, IReadOnlyList<string> paths)
    {
        var note = $"{TamperNoteMarker} — the check changed platform-owned judge bytes while it ran: {Flatten(paths)}";
        var evidence = $"{TamperNoteMarker} — the check changed platform-owned judge bytes while it ran:\n{string.Join('\n', paths)}";

        var voided = grade.Passed ? grade with { Passed = false, Detail = ChangedDuringCheckDetail, Class = GradeFailureClass.Genuine } : grade;

        return voided with
        {
            OracleNote = OracleRuntime.CombineNotes(note, grade.OracleNote),
            EvidenceText = string.IsNullOrEmpty(grade.EvidenceText) ? evidence : $"{evidence}\n{grade.EvidenceText}",
        };
    }

    private static BenchmarkGrade Label(BenchmarkGrade grade, string reason) => grade with { OracleNote = OracleRuntime.CombineNotes(grade.OracleNote, OracleRuntime.UnverifiedNote(reason)) };
}
