using CodeSpace.Core.Jobs;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Backends;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

/// <summary>
/// Pins the committed retention policy, the reaper's bounds, and the Rule 14 job shape. The windows are asserted as
/// literals on purpose: they are the only numbers in the system whose reduction destroys data, so shortening one has to
/// be a deliberate edit here as well as in the policy.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactRetentionPolicyTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1";

    [Fact]
    public void Registered_classes_keep_orphan_candidates_for_a_week_then_quarantine_them_for_a_day()
    {
        var rules = new[] { ArtifactRetentionClass.ArtifactManifestContent, ArtifactRetentionClass.SensitiveRecordPayload }
            .Select(value => ArtifactRetentionPolicy.For(value.ToString()).ShouldNotBeNull()).ToArray();

        rules.ShouldAllBe(rule => rule.MinimumAge == TimeSpan.FromDays(7) && rule.QuarantineWindow == TimeSpan.FromHours(24));
        ArtifactRetentionPolicy.MinimumAgeFloor.ShouldBe(TimeSpan.FromDays(7), "the claim query pre-filters on the smallest floor across all classes");
    }

    [Theory]
    [InlineData("SomeClassARollbackRemoved")]   // the real case: a newer build declared it, this build was rolled back past it
    [InlineData("9999")]                        // a numeric name is not a member name either
    [InlineData("")]                            // an empty column value names nothing
    public void A_class_the_policy_does_not_register_has_no_rule(string unregistered)
    {
        // The reaper reads a null rule as Indeterminate, so an unregistered value can never be collected. The lookup takes
        // the stored NAME, not the enum, precisely so an unknown name reaches this null instead of throwing on the EF read —
        // a throw there would kill the whole sweep batch, which is strictly worse than the keep it was meant to guarantee.
        ArtifactRetentionPolicy.For(unregistered).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 25, 8, 60, 15, 30)]      // batch must be positive
    [InlineData(200, 0, 8, 60, 15, 30)]     // claim size must be positive
    [InlineData(25, 200, 8, 60, 15, 30)]    // a claim wider than the batch would overrun the bound
    [InlineData(200, 25, 0, 60, 15, 30)]    // the attempt budget must exist, else a retry loops forever
    [InlineData(200, 25, 8, 15, 15, 30)]    // a lease no longer than the operation it covers can expire mid-work
    [InlineData(200, 25, 8, 60, 0, 30)]     // an operation must be bounded
    [InlineData(200, 25, 8, 60, 15, 0)]     // a zero retry delay would hot-loop the queue
    public void An_unbounded_reaper_configuration_is_rejected_at_construction(int batch, int claim, int attempts, int leaseSeconds, int operationSeconds, int retryMinutes)
    {
        var options = new ArtifactRetentionReaperOptions(batch, claim, attempts, TimeSpan.FromSeconds(leaseSeconds), TimeSpan.FromSeconds(operationSeconds), TimeSpan.FromMinutes(retryMinutes));

        Should.Throw<ArgumentOutOfRangeException>(() => new ArtifactRetentionReaper(new ArtifactRetentionReaperServices(
            DbOptions(), Oracle(), Blobs(), Purge(), NullLogger<ArtifactRetentionReaper>.Instance), options));
    }

    [Fact]
    public void The_shipped_reaper_configuration_is_accepted()
    {
        Should.NotThrow(() => new ArtifactRetentionReaper(DbOptions(), Oracle(), Blobs(), Purge(), NullLogger<ArtifactRetentionReaper>.Instance));
    }

    [Fact]
    public async Task The_recurring_job_is_a_thin_dispatcher_for_the_bounded_sweep()
    {
        var mediator = new RecordingMediator();
        var job = new ArtifactRetentionReaperRecurringJob(mediator);

        typeof(ArtifactRetentionReaperRecurringJob).GetInterfaces().ShouldContain(typeof(IRecurringJob));
        job.JobId.ShouldBe(nameof(ArtifactRetentionReaperRecurringJob));
        job.CronExpression.ShouldBe("15 * * * *");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<ReapUnreferencedArtifactsCommand>();
    }

    [Fact]
    public void The_store_is_the_declaring_seam_so_a_declaration_can_ride_the_artifact_insert()
    {
        // Rule 7: the declaring write is a SIBLING face on the store, not a widening of IArtifactStore — so every
        // existing PutAsync caller keeps writing bytes that are permanently unreapable.
        typeof(ArtifactStore).GetInterfaces().ShouldContain(typeof(IArtifactRetentionWriter));
        typeof(IArtifactStore).GetMethods().Select(method => method.Name).ShouldNotContain(nameof(IArtifactRetentionWriter.PutDeclaredAsync));
    }

    [Fact]
    public void Every_production_retention_candidate_has_one_oracle_visible_holder_write()
    {
        var sourceRoot = Path.Combine(FindRepoRoot(), "backend", "src", "CodeSpace.Core");
        var callers = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(".PutDeclaredAsync(", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(sourceRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();

        callers.ShouldBe(["Services/Agents/Publish/ArtifactManifestStore.cs", "Services/Workflows/Runtime/WorkflowSensitivePayloadStore.cs"],
            "every retention-candidate id must be freshly obtained through PutDeclaredAsync immediately before its one oracle-visible holder write");
        typeof(IArtifactManifestStore).GetMethod(nameof(IArtifactManifestStore.CaptureDeclaredAsync))!.ReturnType.ShouldBe(typeof(Task<int>),
            "manifest capture must not return the candidate artifact id for a later writer to reuse without passing the store's availability/dedup fence again");
        File.ReadAllText(Path.Combine(sourceRoot, callers[0])).ShouldContain("ContentArtifactId = artifactId");
        File.ReadAllText(Path.Combine(sourceRoot, callers[1])).ShouldContain("CiphertextArtifactId = artifactId");
    }

    [Fact]
    public void The_shipped_blob_backend_can_purge_so_offloaded_bytes_are_reclaimable_at_all()
    {
        // The reaper feature-detects IArtifactBlobPurge off the injected backend and keeps every offloaded artifact
        // forever when it is absent. Dropping the interface from the shipped backend would therefore silently turn the
        // offloaded purge back off with no other test noticing.
        typeof(LocalFileArtifactBlobBackend).GetInterfaces().ShouldContain(typeof(IArtifactBlobPurge));

        // Rule 7: removal is a SIBLING face, never a widening — a write-once transport stays a valid backend.
        typeof(IArtifactBlobBackend).GetMethods().Select(method => method.Name).ShouldNotContain(nameof(IArtifactBlobPurge.DeleteAsync));
    }

    private static DbContextOptions<CodeSpaceDbContext> DbOptions() =>
        new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options;

    private static IArtifactReferenceOracle Oracle() => new ArtifactReferenceOracle(NullLogger<ArtifactReferenceOracle>.Instance);

    private static IArtifactBlobBackend Blobs() => new LocalFileArtifactBlobBackend(Path.Combine(Path.GetTempPath(), $"artifact-retention-{Guid.NewGuid():N}"));

    private static IArtifactCasPurgeCoordinator Purge() => new RefusingCasPurgeCoordinator();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "backend"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private sealed class RefusingCasPurgeCoordinator : IArtifactCasPurgeCoordinator
    {
        private static readonly ArtifactCasProblem Unsupported = new(ArtifactCasProblemCode.Unsupported, false);

        public Task<ArtifactCasPurgeClaimResult> ClaimAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactCasPurgeClaimResult>(new ArtifactCasPurgeClaimResult.Rejected(Unsupported));

        public Task<ArtifactCasPurgeResult> DeleteAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactCasPurgeResult>(new ArtifactCasPurgeResult.Rejected(Unsupported));

        public Task<bool> ReleaseAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactCasPurgeResult>(new ArtifactCasPurgeResult.Rejected(Unsupported));
    }

    private sealed class RecordingMediator : IMediator
    {
        public List<object> Sent { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(default(TResponse)!);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult<object?>(null);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Sent.Add(request);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }
}
