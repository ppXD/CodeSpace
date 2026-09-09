using System.Text;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class BenchmarkGradeEvidenceExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cs-grade-export-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Complete_evidence_has_distinct_source_and_redacted_file_hashes_and_no_private_values()
    {
        var secrets = new[] { "private-model", "https://private-endpoint.invalid", "private-key-秘密" };
        var bytes = Encoding.UTF8.GetBytes("grade failed: " + string.Join(" | ", secrets));
        var reader = new MemoryRangeReader(bytes);
        var result = await ExportAsync(reader, secrets);
        var exported = File.ReadAllBytes(Path.Combine(_directory, "grade.txt"));
        result.Availability.ShouldBe("available");
        result.Partial.ShouldBeFalse();
        result.SourceLength.ShouldBe(bytes.LongLength);
        result.ReadByteCount.ShouldBe(bytes.Length);
        result.SourceBytesRepresented.ShouldBe(bytes.Length);
        result.SourceDeclaredSha256.ShouldBe(BenchmarkEvidenceExport.Hash(bytes));
        result.ReadSourceSha256.ShouldBe(result.SourceDeclaredSha256);
        result.ExportedSha256.ShouldBe(BenchmarkEvidenceExport.Hash(exported));
        result.ExportedSha256.ShouldNotBe(result.SourceDeclaredSha256);
        Encoding.UTF8.GetString(exported).ShouldBe("grade failed: *** | *** | ***");
        reader.RequestedLength.ShouldBe(BenchmarkEvidenceOptions.GradeReadLimitBytes);
    }

    [Fact]
    public async Task Credential_normalization_cannot_expose_an_endpoint_without_its_configured_trailing_slash()
    {
        var bytes = Encoding.UTF8.GetBytes("https://private-endpoint.invalid/v1 | https://private-endpoint.invalid | private-endpoint.invalid | private-model");
        await ExportAsync(new MemoryRangeReader(bytes), new[] { " https://private-endpoint.invalid/v1/ ", " private-model " });
        File.ReadAllText(Path.Combine(_directory, "grade.txt")).ShouldBe("*** | *** | *** | ***");
    }

    [Fact]
    public async Task A_secret_crossing_the_prefix_boundary_is_withheld_without_flushing_its_partial_bytes()
    {
        const string secret = "secret-that-crosses-the-reader-boundary";
        var bytes = Encoding.UTF8.GetBytes(new string('x', BenchmarkEvidenceOptions.GradeReadLimitBytes - 5) + secret + "diagnostic tail");
        var result = await ExportAsync(new MemoryRangeReader(bytes), new[] { secret });
        var text = File.ReadAllText(Path.Combine(_directory, "grade.txt"));
        result.Partial.ShouldBeTrue();
        result.ReadByteCount.ShouldBe(BenchmarkEvidenceOptions.GradeReadLimitBytes);
        result.SourceBytesRepresented.ShouldBeLessThan(result.ReadByteCount);
        result.SourceLength.ShouldBe(bytes.LongLength);
        text.ShouldNotContain("secre");
        text.ShouldAllBe(c => c == 'x');
    }

    [Fact]
    public async Task An_oversized_secret_suppresses_text_and_still_records_the_actual_metadata_probe_read()
    {
        var reader = new MemoryRangeReader(Encoding.UTF8.GetBytes("grader output"));
        var result = await ExportAsync(reader, new[] { new string('s', BenchmarkEvidenceOptions.MaximumSecretBytes + 1) });
        result.Availability.ShouldBe("unknown-secret-exceeds-export-bound");
        result.TextFile.ShouldBeNull();
        result.ExportedSha256.ShouldBeNull();
        result.ReadByteCount.ShouldBe(1);
        reader.RequestedLength.ShouldBe(1);
        File.Exists(Path.Combine(_directory, "grade.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task Invalid_utf8_is_marked_as_lossy_rendering_with_a_hash_of_the_actual_exported_bytes()
    {
        byte[] source = [65, 0xff, 66];
        var result = await ExportAsync(new MemoryRangeReader(source), []);
        result.LossyUtf8Rendering.ShouldBeTrue();
        result.ReadSourceSha256.ShouldBe(BenchmarkEvidenceExport.Hash(source));
        result.ExportedSha256.ShouldBe(BenchmarkEvidenceExport.Hash(File.ReadAllBytes(Path.Combine(_directory, "grade.txt"))));
        result.ExportedSha256.ShouldNotBe(result.ReadSourceSha256);
    }

    [Theory]
    [InlineData(ArtifactRangeReadState.MetadataMissing)]
    [InlineData(ArtifactRangeReadState.PhysicalObjectMissing)]
    [InlineData(ArtifactRangeReadState.IntegrityFailure)]
    [InlineData(ArtifactRangeReadState.BackendUnavailable)]
    [InlineData(ArtifactRangeReadState.AccessDenied)]
    public async Task An_unavailable_or_unverified_artifact_has_no_fabricated_text_or_hash(ArtifactRangeReadState state)
    {
        var result = await ExportAsync(new MemoryRangeReader([]) { Failure = state }, []);
        result.Availability.ShouldBe(state.ToString());
        result.TextFile.ShouldBeNull();
        result.ExportedSha256.ShouldBeNull();
        File.Exists(Path.Combine(_directory, "grade.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_reader_exception_is_unknown_without_exporting_its_secret_bearing_message()
    {
        var result = await ExportAsync(new MemoryRangeReader([]) { Throw = true }, new[] { "private-key" });
        result.Availability.ShouldBe("unknown-read-failed");
        result.TextFile.ShouldBeNull();
        result.ToString().ShouldNotContain("private-key");
    }

    [Fact]
    public async Task A_filesystem_failure_is_not_silently_reported_as_an_exported_artifact()
    {
        File.WriteAllText(_directory, "this path is a file");
        await Assert.ThrowsAsync<IOException>(() => ExportAsync(new MemoryRangeReader(Encoding.UTF8.GetBytes("grade")), []));
    }

    [Fact]
    public void Both_live_consumers_export_before_grading_gates_and_the_job_uses_an_absolute_artifact_directory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, ".github", "workflows", "real-model.yml"))) root = root.Parent;
        root.ShouldNotBeNull();
        var tests = Path.Combine(root.FullName, "backend", "tests", "CodeSpace.E2ETests", "Workflows");
        foreach (var name in new[] { "RealModelBenchmarkCorpusE2ETests.cs", "RealModelBenchmarkHiddenFixtureE2ETests.cs" })
            File.ReadAllText(Path.Combine(tests, name)).ShouldContain("BenchmarkEvidenceExport.RunAsync(");
        var workflow = File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "real-model.yml"));
        var start = workflow.IndexOf("  real-model-benchmark:", StringComparison.Ordinal);
        var end = workflow.IndexOf("\n  real-model-", start + 1, StringComparison.Ordinal);
        var job = end < 0 ? workflow[start..] : workflow[start..end];
        job.ShouldContain("CODESPACE_BENCHMARK_EVIDENCE_DIRECTORY: ${{ github.workspace }}/backend/TestResults/benchmark-evidence");
        job.ShouldContain("path: backend/TestResults/");
        job.ShouldContain("if: always()");
    }

    [Fact]
    public void Paired_TaskLaunch_exports_cell_diagnostics_to_the_uploaded_absolute_directory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, ".github", "workflows", "real-model.yml"))) root = root.Parent;
        root.ShouldNotBeNull();
        var source = File.ReadAllText(Path.Combine(root.FullName, "backend", "tests", "CodeSpace.E2ETests", "Workflows", "RealModelQualificationRehearsalE2ETests.cs"));
        source.ShouldContain("CODESPACE_QUALIFICATION_EVIDENCE_DIRECTORY");
        source.ShouldContain("cellOrdinal");
        source.ShouldContain("outcome.Control.Solved");
        source.ShouldContain("outcome.Control.CostKnownCells");

        var workflow = File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "real-model.yml"));
        var start = workflow.IndexOf("  real-model-qualification-rehearsal:", StringComparison.Ordinal);
        var end = workflow.IndexOf("\n  real-model-", start + 1, StringComparison.Ordinal);
        var job = end < 0 ? workflow[start..] : workflow[start..end];
        job.ShouldContain("CODESPACE_QUALIFICATION_EVIDENCE_DIRECTORY: ${{ github.workspace }}/backend/TestResults/qualification-evidence");
        job.ShouldContain("test -s backend/TestResults/qualification-evidence/paired-tasklaunch-development-protocol.json");
        job.ShouldContain("path: backend/TestResults/");
    }

    [Theory]
    [InlineData("development-protocol", "development-protocol")]
    [InlineData("../../hidden suite", "------hidden-suite")]
    [InlineData("", "suite")]
    public void Paired_evidence_file_names_cannot_escape_the_artifact_directory(string input, string expected) =>
        RealModelQualificationRehearsalE2ETests.SafeEvidenceFilePart(input).ShouldBe(expected);

    [Fact]
    public void Paired_evidence_uses_an_absolute_operator_configured_directory() =>
        RealModelQualificationRehearsalE2ETests.QualificationEvidenceDirectory("relative-evidence").ShouldBe(Path.GetFullPath("relative-evidence"));

    private Task<GradeEvidenceProjection> ExportAsync(IArtifactRangeReader reader, IReadOnlyList<string> secrets) => BenchmarkGradeEvidenceExport.ReadAsync(reader, new BenchmarkGrade { Passed = false, Detail = "failed", EvidenceArtifactId = Guid.NewGuid() }, new GradeEvidenceRequest(Guid.NewGuid(), _directory, "grade.txt", secrets), CancellationToken.None);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); else if (File.Exists(_directory)) File.Delete(_directory); }

    private sealed class MemoryRangeReader(byte[] bytes) : IArtifactRangeReader
    {
        public int RequestedLength { get; private set; }
        public ArtifactRangeReadState? Failure { get; init; }
        public bool Throw { get; init; }
        public Task<ArtifactRangeReadResult> ReadRangeAsync(Guid teamId, Guid artifactId, long offset, int length, CancellationToken cancellationToken)
        {
            offset.ShouldBe(0);
            RequestedLength = length;
            if (Throw) throw new IOException("private-key backend failure");
            return Task.FromResult(Failure is { } state ? ArtifactRangeReadResult.Failed(state) : ArtifactRangeReadResult.Available(bytes.Take(length).ToArray(), bytes.LongLength, BenchmarkEvidenceExport.Hash(bytes), "text/plain", bytes.Length <= length));
        }
        public Task<IReadOnlyDictionary<Guid, ArtifactRangeReadResult>> ReadRangesAsync(ArtifactRangesReadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
