using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.E2ETests.Workflows;

internal sealed record GradeEvidenceRequest(Guid TeamId, string Directory, string FileName, IReadOnlyList<string> KnownSecrets);

internal sealed record GradeEvidenceProjection
{
    public required string Availability { get; init; }
    public Guid? ArtifactId { get; init; }
    public long? SourceLength { get; init; }
    public string? SourceDeclaredSha256 { get; init; }
    public int ReadByteCount { get; init; }
    public string? ReadSourceSha256 { get; init; }
    public int SourceBytesRepresented { get; init; }
    public int ExportedByteCount { get; init; }
    public string? ExportedSha256 { get; init; }
    public bool Partial { get; init; }
    public bool IntegrityVerified { get; init; }
    public bool LossyUtf8Rendering { get; init; }
    public string? TextFile { get; init; }
}

internal static class BenchmarkGradeEvidenceExport
{
    internal static async Task<GradeEvidenceProjection> ReadAsync(IArtifactRangeReader reader, BenchmarkGrade? grade, GradeEvidenceRequest request, CancellationToken cancellationToken)
    {
        if (grade?.EvidenceArtifactId is not { } id) return new GradeEvidenceProjection { Availability = "unknown-no-artifact-reference" };
        var needles = BenchmarkEvidenceExport.RedactionNeedles(request.KnownSecrets);
        var oversizedSecret = needles.Any(s => Encoding.UTF8.GetByteCount(s) > BenchmarkEvidenceOptions.MaximumSecretBytes);
        ArtifactRangeReadResult range;
        try { range = await reader.ReadRangeAsync(request.TeamId, id, 0, oversizedSecret ? 1 : BenchmarkEvidenceOptions.GradeReadLimitBytes, cancellationToken).ConfigureAwait(false); }
        catch (Exception) { return new GradeEvidenceProjection { ArtifactId = id, Availability = "unknown-read-failed" }; }

        var record = new GradeEvidenceProjection { ArtifactId = id, Availability = range.State.ToString(), SourceLength = range.TotalLength, SourceDeclaredSha256 = range.Sha256, IntegrityVerified = range.IntegrityVerified, ReadByteCount = range.Bytes?.Length ?? 0 };
        if (range.State != ArtifactRangeReadState.Available || range.Bytes is null) return record;
        if (oversizedSecret) return record with { Availability = "unknown-secret-exceeds-export-bound", Partial = true };
        if (range.Bytes.Length > BenchmarkEvidenceOptions.GradeReadLimitBytes) return record with { Availability = "unknown-range-exceeded-bound", Partial = true };

        var complete = range.TotalLength == range.Bytes.LongLength;
        // On a prefix read, do not flush the suffix: it may be the beginning of a configured secret.
        var transformed = new SecretRedactor(needles).CreateUtf8Stream().Transform(range.Bytes, final: complete);
        var text = Encoding.UTF8.GetString(transformed.Bytes.Span);
        var bytes = Encoding.UTF8.GetBytes(text);
        BenchmarkEvidenceExport.WriteFile(Path.Combine(request.Directory, request.FileName), bytes);
        return record with
        {
            Availability = "available", ReadByteCount = range.Bytes.Length, ReadSourceSha256 = BenchmarkEvidenceExport.Hash(range.Bytes),
            SourceBytesRepresented = transformed.SourceBytesConsumed, ExportedByteCount = bytes.Length, ExportedSha256 = BenchmarkEvidenceExport.Hash(bytes),
            Partial = !complete || transformed.SourceBytesConsumed < range.Bytes.Length, LossyUtf8Rendering = !bytes.AsSpan().SequenceEqual(transformed.Bytes.Span), TextFile = request.FileName,
        };
    }
}
