using System.Security.Cryptography;
using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

public sealed partial class ArtifactStoreRoutedDestinationFlowTests
{
    [Theory]
    [InlineData("inline")]
    [InlineData("empty")]
    [InlineData("local")]
    [InlineData("routed")]
    public async Task Strict_content_verification_returns_only_the_fresh_complete_recorded_identity(string destination)
    {
        var stored = await SeedStrictArtifactAsync(destination);
        using var scope = _fixture.BeginScope();
        var receipt = await scope.Resolve<IArtifactContentVerifier>().VerifyAsync(stored.Request, CancellationToken.None);
        receipt.ShouldBe(new ArtifactVerifiedContent(stored.Request.TeamId, stored.Request.ArtifactId, stored.Request.Sha256, stored.Request.SizeBytes));
    }

    [Theory]
    [InlineData("foreign-team", ArtifactContentUnavailableKind.MetadataMissing)]
    [InlineData("missing-id", ArtifactContentUnavailableKind.MetadataMissing)]
    [InlineData("wrong-sha", ArtifactContentUnavailableKind.IntegrityFailure)]
    [InlineData("wrong-size", ArtifactContentUnavailableKind.IntegrityFailure)]
    public async Task Strict_content_verification_requires_team_visible_metadata_matching_the_expected_receipt(string mismatch, ArtifactContentUnavailableKind kind)
    {
        var stored = await SeedStrictArtifactAsync("local");
        var request = mismatch switch
        {
            "foreign-team" => stored.Request with { TeamId = Guid.NewGuid() },
            "missing-id" => stored.Request with { ArtifactId = Guid.NewGuid() },
            "wrong-sha" => stored.Request with { Sha256 = new string('0', 64) },
            _ => stored.Request with { SizeBytes = stored.Request.SizeBytes + 1 },
        };
        using var scope = _fixture.BeginScope();
        (await Should.ThrowAsync<ArtifactContentUnavailableException>(() => scope.Resolve<IArtifactContentVerifier>().VerifyAsync(request, CancellationToken.None))).Kind.ShouldBe(kind);
    }

    [Theory]
    [InlineData("local", "missing", ArtifactContentUnavailableKind.PhysicalObjectMissing)]
    [InlineData("local", "same-size", ArtifactContentUnavailableKind.IntegrityFailure)]
    [InlineData("local", "short", ArtifactContentUnavailableKind.IntegrityFailure)]
    [InlineData("local", "long", ArtifactContentUnavailableKind.IntegrityFailure)]
    [InlineData("routed", "missing", ArtifactContentUnavailableKind.PhysicalObjectMissing)]
    [InlineData("routed", "same-size", ArtifactContentUnavailableKind.IntegrityFailure)]
    [InlineData("routed", "short", ArtifactContentUnavailableKind.IntegrityFailure)]
    [InlineData("routed", "long", ArtifactContentUnavailableKind.IntegrityFailure)]
    public async Task Strict_content_verification_rejects_missing_corrupt_short_and_long_physical_objects(string destination, string damage, ArtifactContentUnavailableKind kind)
    {
        var stored = await SeedStrictArtifactAsync(destination);
        if (damage == "missing") File.Delete(stored.Path!);
        else
        {
            var length = (int)stored.Request.SizeBytes + (damage == "short" ? -1 : damage == "long" ? 1 : 0);
            await File.WriteAllBytesAsync(stored.Path!, new byte[length]);
        }
        using var scope = _fixture.BeginScope();
        (await Should.ThrowAsync<ArtifactContentUnavailableException>(() => scope.Resolve<IArtifactContentVerifier>().VerifyAsync(stored.Request, CancellationToken.None))).Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Strict_content_verification_rejects_corrupt_inline_bytes_even_when_metadata_is_unchanged()
    {
        var stored = await SeedStrictArtifactAsync("inline");
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact DISABLE TRIGGER workflow_artifact_enforce_immutability");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_artifact SET inline_bytes = {new byte[stored.Request.SizeBytes]} WHERE id = {stored.Request.ArtifactId}");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact ENABLE TRIGGER workflow_artifact_enforce_immutability");
            await transaction.CommitAsync();
        }
        (await Should.ThrowAsync<ArtifactContentUnavailableException>(() => scope.Resolve<IArtifactContentVerifier>().VerifyAsync(stored.Request, CancellationToken.None))).Kind.ShouldBe(ArtifactContentUnavailableKind.IntegrityFailure);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("routed")]
    public async Task Strict_content_verification_reads_the_recorded_route_and_never_falls_back_to_a_new_destination(string destination)
    {
        var stored = await SeedStrictArtifactAsync(destination);
        var replacementRoot = NewRoot();
        var replacementProfile = await SeedProfileAsync(stored.Request.TeamId, replacementRoot);
        if (destination == "local") await SeedRouteAsync(stored.Request.TeamId, replacementProfile);
        else await RepointRouteAsync(stored.Request.TeamId, replacementProfile);
        var replacementPath = ObjectPath(replacementRoot, stored.Request.Sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(replacementPath)!);
        File.Copy(stored.Path!, replacementPath);
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IArtifactContentVerifier>().VerifyAsync(stored.Request, CancellationToken.None)).ArtifactId.ShouldBe(stored.Request.ArtifactId);
        File.Delete(stored.Path!);
        (await Should.ThrowAsync<ArtifactContentUnavailableException>(() => scope.Resolve<IArtifactContentVerifier>().VerifyAsync(stored.Request, CancellationToken.None))).Kind.ShouldBe(ArtifactContentUnavailableKind.PhysicalObjectMissing);
        File.Exists(replacementPath).ShouldBeTrue("the current destination has correct bytes, but this artifact never recorded it");
    }

    [Theory]
    [InlineData("eof", false)]
    [InlineData("cancel", false)]
    [InlineData("short", false)]
    [InlineData("long", false)]
    [InlineData("cancel", true)]
    [InlineData("short", true)]
    [InlineData("long", true)]
    public async Task Strict_content_verification_waits_for_eof_bounds_reads_and_disposes_on_each_outcome(string mode, bool disposalFault)
    {
        var stored = await SeedStrictArtifactAsync("local");
        var stream = new ControlledReadStream(File.OpenRead(stored.Path!), stored.Request.SizeBytes, mode, disposalFault);
        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(new StrictReadBackend(stream)).As<IArtifactBlobBackend>());
        using var cancellation = new CancellationTokenSource();
        var verification = scope.Resolve<IArtifactContentVerifier>().VerifyAsync(stored.Request, cancellation.Token);
        if (mode is "eof" or "cancel")
        {
            await stream.EofRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            verification.IsCompleted.ShouldBeFalse("reading the claimed number of bytes is not an observed EOF");
            if (mode == "cancel") cancellation.Cancel();
            else stream.ReleaseEof.TrySetResult();
        }
        if (mode == "eof") (await verification).ArtifactId.ShouldBe(stored.Request.ArtifactId);
        else if (mode == "cancel")
        {
            // Await the API directly: Shouldly's cancelled-task helper can manufacture a TaskCanceledException,
            // which does not carry diagnostics on the exception the production caller actually receives.
            OperationCanceledException? cancelled = null;
            try { await verification; }
            catch (OperationCanceledException ex) { cancelled = ex; }
            cancelled.ShouldNotBeNull();
            if (disposalFault) cancelled.Data["ArtifactContentReadCleanupFailure"].ShouldBeOfType<IOException>().Message.ShouldBe("provider-close-failed");
        }
        else
        {
            var failure = await Should.ThrowAsync<ArtifactContentUnavailableException>(() => verification);
            failure.Kind.ShouldBe(ArtifactContentUnavailableKind.IntegrityFailure);
            if (disposalFault) failure.InnerException!.Data["ArtifactContentReadCleanupFailure"].ShouldBeOfType<IOException>().Message.ShouldBe("provider-close-failed");
        }
        stream.Disposed.ShouldBeTrue();
        stream.LargestRequest.ShouldBeLessThanOrEqualTo(128 * 1024);
        stream.ObservedBytes.ShouldBeLessThanOrEqualTo(stored.Request.SizeBytes + 1, "an overlong stream must be rejected after the first extra byte");
        if (mode == "eof") stream.EofObserved.ShouldBeTrue();
    }

    [Fact]
    public async Task Strict_content_verification_never_substitutes_a_whole_byte_or_ui_range_read_for_missing_stream_capability()
    {
        var stored = await SeedStrictArtifactAsync("local");
        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(new NoStreamBackend()).As<IArtifactBlobBackend>());
        (await Should.ThrowAsync<ArtifactContentUnavailableException>(() => scope.Resolve<IArtifactContentVerifier>().VerifyAsync(stored.Request, CancellationToken.None))).Kind.ShouldBe(ArtifactContentUnavailableKind.BackendUnavailable);
    }

    [Fact]
    public async Task Strict_content_verification_honors_precancel_before_any_storage_access()
    {
        var stored = await SeedStrictArtifactAsync("local");
        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(new NoStreamBackend()).As<IArtifactBlobBackend>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => scope.Resolve<IArtifactContentVerifier>().VerifyAsync(stored.Request, cancellation.Token));
    }

    [Fact]
    public async Task Strict_content_verification_does_not_reuse_a_callers_old_inline_transaction_snapshot()
    {
        var stored = await SeedStrictArtifactAsync("inline");
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        (await db.WorkflowArtifact.AsNoTracking().SingleAsync(row => row.Id == stored.Request.ArtifactId)).InlineBytes.ShouldNotBeNull();
        using (var deletion = _fixture.BeginScope())
        {
            var deleteDb = deletion.Resolve<CodeSpaceDbContext>();
            await using var deleteTransaction = await deleteDb.Database.BeginTransactionAsync();
            await deleteDb.Database.ExecuteSqlRawAsync("SET LOCAL codespace.artifact_purge_allowed = 'on'");
            await deleteDb.WorkflowArtifact.Where(row => row.Id == stored.Request.ArtifactId).ExecuteDeleteAsync();
            await deleteTransaction.CommitAsync();
        }
        (await Should.ThrowAsync<ArtifactContentUnavailableException>(() => scope.Resolve<IArtifactContentVerifier>().VerifyAsync(stored.Request, CancellationToken.None))).Kind.ShouldBe(ArtifactContentUnavailableKind.MetadataMissing);
        (await db.WorkflowArtifact.AsNoTracking().SingleAsync(row => row.Id == stored.Request.ArtifactId)).InlineBytes.ShouldNotBeNull("the verifier must not mutate or replace the caller's still-valid old snapshot");
    }

    [Fact]
    public async Task Strict_content_verification_requires_committed_metadata_and_leaves_the_callers_transaction_intact()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var content = RandomNumberGenerator.GetBytes(100);
        var id = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "application/octet-stream", CancellationToken.None);
        var request = new ArtifactContentVerificationRequest(teamId, id, ArtifactStore.ComputeSha256Hex(content), content.Length);
        (await Should.ThrowAsync<ArtifactContentUnavailableException>(() => scope.Resolve<IArtifactContentVerifier>().VerifyAsync(request, CancellationToken.None))).Kind.ShouldBe(ArtifactContentUnavailableKind.MetadataMissing);
        db.Database.CurrentTransaction.ShouldBeSameAs(transaction);
        await transaction.CommitAsync();
        (await scope.Resolve<IArtifactContentVerifier>().VerifyAsync(request, CancellationToken.None)).ArtifactId.ShouldBe(id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Strict_content_verification_of_a_large_local_or_inline_object_does_not_allocate_the_payload(bool inline)
    {
        const int size = 32 * 1024 * 1024;
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var path = Path.Combine(root, "large-object");
        var buffer = Enumerable.Repeat((byte)'z', 128 * 1024).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var output = File.Create(path))
            for (var written = 0; written < size; written += buffer.Length) { await output.WriteAsync(buffer); hash.AppendData(buffer); }
        var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        var id = Guid.NewGuid();
        using var scope = _fixture.BeginScope(builder =>
        {
            if (!inline) builder.RegisterInstance(new StrictReadBackend(new ControlledReadStream(File.OpenRead(path), size, "normal"))).As<IArtifactBlobBackend>();
        });
        var db = scope.Resolve<CodeSpaceDbContext>();
        if (inline)
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO workflow_artifact (id,team_id,sha256,content_type,size_bytes,inline_bytes,created_at) VALUES ({id},{teamId},{sha},'application/octet-stream',{size},convert_to(repeat('z',{size}),'UTF8'),clock_timestamp())");
        else
        {
            db.WorkflowArtifact.Add(new WorkflowArtifact { Id = id, TeamId = teamId, Sha256 = sha, ContentType = "application/octet-stream", SizeBytes = size, StorageUrl = new Uri(path).AbsoluteUri, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var verifier = scope.Resolve<IArtifactContentVerifier>();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var receipt = await verifier.VerifyAsync(new(teamId, id, sha, size), CancellationToken.None);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        receipt.SizeBytes.ShouldBe(size);
        allocated.ShouldBeLessThan(8L * 1024 * 1024, "the 32 MiB payload must never materialize in the backend process, including the PostgreSQL inline lane");
    }

    private async Task<StrictStoredArtifact> SeedStrictArtifactAsync(string destination)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = destination == "routed" ? NewRoot() : null;
        if (root != null) await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = RandomNumberGenerator.GetBytes(destination == "empty" ? 0 : destination == "inline" ? 100 : 20_000);
        var id = await PutAsync(teamId, content);
        var row = await RowAsync(id);
        var path = root != null ? ObjectPath(root, row.Sha256) : row.StorageUrl is { } url ? new Uri(url).LocalPath : null;
        return new(new(teamId, id, row.Sha256, row.SizeBytes), path);
    }

    private sealed record StrictStoredArtifact(ArtifactContentVerificationRequest Request, string? Path);

    private class NoStreamBackend : IArtifactBlobBackend
    {
        public Task<string> WriteAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("verification cannot write");
        public Task<bool> ExistsAsync(string storageUrl, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Exists is not verification");
        public Task<byte[]> ReadAsync(string storageUrl, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("whole-byte materialization is forbidden");
        public Task<ArtifactBlobRange> ReadRangeAsync(string storageUrl, long offset, int length, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("UI range reads are not authoritative");
    }

    private sealed class StrictReadBackend(Stream stream) : NoStreamBackend, IArtifactBlobStreamReader
    {
        public Task<Stream> OpenReadAsync(string storageUrl, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(stream); }
    }

    private sealed class ControlledReadStream(Stream inner, long expected, string mode, bool disposalFault = false) : Stream
    {
        public TaskCompletionSource EofRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEof { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long ObservedBytes { get; private set; }
        public int LargestRequest { get; private set; }
        public bool Disposed { get; private set; }
        public bool EofObserved { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestRequest = Math.Max(LargestRequest, buffer.Length);
            if (mode == "short" && ObservedBytes == expected - 1) return 0;
            var length = mode == "short" ? (int)Math.Min(buffer.Length, expected - 1 - ObservedBytes) : buffer.Length;
            var read = await inner.ReadAsync(buffer[..length], cancellationToken);
            if (read == 0 && mode == "long") { buffer.Span[0] = 0; read = 1; }
            if (read == 0)
            {
                EofRequested.TrySetResult();
                if (mode is "eof" or "cancel") await ReleaseEof.Task.WaitAsync(cancellationToken);
                EofObserved = true;
            }
            ObservedBytes += read;
            return read;
        }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("a length property is not an EOF observation");
        public override long Position { get => ObservedBytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { Disposed = true; inner.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { Disposed = true; await inner.DisposeAsync(); GC.SuppressFinalize(this); if (disposalFault) throw new IOException("provider-close-failed"); }
    }
}
