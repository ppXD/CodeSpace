using System.Security.Cryptography;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Adds provider-neutral CAS observations beside immutable pre-CAS artifact rows. The evidence pass writes no CAS data-plane rows;
/// the minting pass revalidates one object witness under the same exact profile revision before every bounded page.
/// Provider I/O and stream hashing finish before the short database transaction starts.
/// </summary>
public sealed class LegacyPlacementAdopter : ILegacyPlacementAdopter
{
    private const int HashBufferSize = 128 * 1024;
    private const int MaximumCommitAttempts = 3;
    private const int TerminalCleanupBatch = 32;
    private static readonly TimeSpan ArcTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromDays(30);
    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly ILegacyPlacementAdoptionRuntime _runtime;
    private readonly IDataProtector _cursorProtector;
    private readonly ILogger<LegacyPlacementAdopter> _logger;

    public LegacyPlacementAdopter(DbContextOptions<CodeSpaceDbContext> dbOptions, ILegacyPlacementAdoptionRuntime runtime, IDataProtectionProvider dataProtectionProvider, ILogger<LegacyPlacementAdopter> logger)
    {
        _dbOptions = dbOptions;
        _runtime = runtime;
        _cursorProtector = dataProtectionProvider.CreateProtector(LegacyPlacementAdoptionCursor.ProtectorPurpose);
        _logger = logger;
    }

    public async Task<LegacyPlacementAdoptionSummary> AdoptAsync(LegacyPlacementAdoptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty || request.ActorId == Guid.Empty || request.ProfileId == Guid.Empty)
            throw new ArgumentException("Legacy placement adoption requires persisted team, actor, and profile identities.", nameof(request));

        var terminalReplay = await ReplayTerminalCursorAsync(request, cancellationToken).ConfigureAwait(false);
        if (terminalReplay != null) return terminalReplay;
        var target = await TargetAsync(request.TeamId, request.ProfileId, cancellationToken).ConfigureAwait(false);
        if (target == null) return await HandleMissingTargetAsync(request, cancellationToken).ConfigureAwait(false);

        var arc = await ResolveArcAsync(request, target, cancellationToken).ConfigureAwait(false);
        if (arc.Summary != null) return arc.Summary;
        if (arc.Cursor == null) return Empty(target, arc.Refusal, arc.Phase);
        if (arc.ResumeOnly) return Current(target, arc.Cursor, arc.Refusal);
        if (arc.Cursor.Mode == LegacyPlacementAdoptionCursorMode.Cleaning)
            return await CleanAsync(request, target, arc.Cursor, cancellationToken).ConfigureAwait(false);
        if (target.State == StorageProfileState.Retired) return Empty(target, LegacyPlacementAdoptionRefusalValue.ProfileRetired);

        var module = _runtime.Modules.Get(target.ProviderTypeKey);
        if (module is not IStorageProviderLegacyLayout layout)
            return await RefuseActiveArcAsync(request, target, arc.Cursor,
                LegacyPlacementAdoptionRefusalValue.ProviderHasNoLegacyLayout, cancellationToken).ConfigureAwait(false);
        if (!module.Capabilities.HasFlag(StorageProviderCapabilities.StreamingRead))
            return await RefuseActiveArcAsync(request, target, arc.Cursor,
                LegacyPlacementAdoptionRefusalValue.ProviderHasNoStreamingRead, cancellationToken).ConfigureAwait(false);
        if (!module.Capabilities.HasFlag(StorageProviderCapabilities.HealthProbe))
            return await RefuseActiveArcAsync(request, target, arc.Cursor,
                LegacyPlacementAdoptionRefusalValue.ProviderHasNoHealthProbe, cancellationToken).ConfigureAwait(false);
        var configuration = Configuration(target.NonSecretConfigJson);

        var claim = await ClaimArcAsync(request.TeamId, target, arc.Cursor, cancellationToken).ConfigureAwait(false);
        if (claim.Summary != null) return claim.Summary;
        if (claim.Claim == null) return Current(target, claim.Cursor ?? arc.Cursor, claim.Refusal);
        var budget = new LegacyPlacementPassBudget(request.ByteBudget, request.TimeBudget, _runtime.Clock);
        var examined = 0;
        try
        {
            var page = await PageAsync(claim.Claim.Cursor, Math.Clamp(request.BatchSize, 1, LegacyPlacementAdoptionLimits.MaxRowsPerPass), cancellationToken).ConfigureAwait(false);
            examined = page.Rows.Count;
            if (page.Rows.Count == 0)
                return await FinishEmptyPageAsync(request, target, claim.Claim, cancellationToken).ConfigureAwait(false);

            var resolution = await OpenDriverAsync(new StorageRuntimeDriverRequest(request.TeamId, request.ProfileId, target.Revision,
                StorageProfileEligibility.Read), claim.Claim, cancellationToken).ConfigureAwait(false);
            if (resolution is not StorageRuntimeDriverResolution.Ready ready)
                return await RetryClaimAsync(target, claim.Claim, page.Rows.Count, cancellationToken).ConfigureAwait(false);

            if (!ready.Lease.Driver.Capabilities.HasFlag(StorageProviderCapabilities.StreamingRead))
            {
                await DisposeAsync(ready.Lease).ConfigureAwait(false);
                return await AbortAsync(new AbortRequest
                {
                    Target = target, Claim = claim.Claim, Refusal = LegacyPlacementAdoptionRefusalValue.ProviderHasNoStreamingRead,
                    Examined = page.Rows.Count,
                }, cancellationToken).ConfigureAwait(false);
            }
            if (!ready.Lease.Driver.Capabilities.HasFlag(StorageProviderCapabilities.HealthProbe))
            {
                await DisposeAsync(ready.Lease).ConfigureAwait(false);
                return await AbortAsync(new AbortRequest
                {
                    Target = target, Claim = claim.Claim, Refusal = LegacyPlacementAdoptionRefusalValue.ProviderHasNoHealthProbe,
                    Examined = page.Rows.Count,
                }, cancellationToken).ConfigureAwait(false);
            }

            if (claim.Claim.Cursor.Mode == LegacyPlacementAdoptionCursorMode.Evidence)
                return await DiscoverAsync(new DiscoveryRequest
                {
                    Request = request, Target = target, Claim = claim.Claim, Page = page,
                    Layout = layout, Configuration = configuration, Lease = ready.Lease, Budget = budget,
                }, cancellationToken).ConfigureAwait(false);

            return await MintAsync(new MintRequest
            {
                Request = request, Target = target, Claim = claim.Claim, Page = page,
                Layout = layout, Configuration = configuration, Lease = ready.Lease, Budget = budget,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (LegacyProviderRejectedException)
        {
            return await AbortAsync(new AbortRequest
            {
                Target = target, Claim = claim.Claim, Refusal = LegacyPlacementAdoptionRefusalValue.ProviderRejected,
                Examined = examined,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await ReleaseAfterFailureAsync(claim.Claim, target, FailureCode(exception, cancellationToken)).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<LegacyPlacementAdoptionSummary?> ReplayTerminalCursorAsync(LegacyPlacementAdoptionRequest request, CancellationToken cancellationToken)
    {
        if (request.Cursor == null) return null;
        if (!LegacyPlacementAdoptionCursor.TryDecode(request.Cursor, request.ProfileId, _cursorProtector, out var cursor))
            return Empty(request.ProfileId, LegacyPlacementAdoptionRefusalValue.CursorInvalid);

        await using var db = CreateDb();
        var arc = await db.LegacyPlacementAdoptionArc.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == cursor.ArcId && value.TeamId == request.TeamId
                && value.StorageProfileId == request.ProfileId, cancellationToken).ConfigureAwait(false);
        return arc != null && IsTerminal(arc.State) ? StoredSummary(arc) : null;
    }

    private async Task<LegacyPlacementAdoptionSummary> HandleMissingTargetAsync(LegacyPlacementAdoptionRequest request, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await TakeTeamArcLockAsync(db, request.TeamId, cancellationToken).ConfigureAwait(false);
        var live = await db.LegacyPlacementAdoptionArc.FromSqlInterpolated($$"""
            SELECT arc.*, arc.xmin FROM legacy_placement_adoption_arc arc
            WHERE arc.team_id = {{request.TeamId}} AND arc.state IN ('Active', 'Cleaning')
            FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (live == null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Empty(request.ProfileId, LegacyPlacementAdoptionRefusalValue.ProfileMissing);
        }
        if (live.StorageProfileId != request.ProfileId)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Empty(request.ProfileId, LegacyPlacementAdoptionRefusalValue.ArcAlreadyActive);
        }

        var liveTarget = await StoredTargetAsync(db, live, cancellationToken).ConfigureAwait(false);
        var resolution = await CloseLockedArcAsync(db, live, new ArcClosure
        {
            Target = liveTarget, Refusal = LegacyPlacementAdoptionRefusalValue.ProfileMissing,
            TerminalState = LegacyPlacementAdoptionArcState.Stale,
        }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (resolution.Summary != null) return resolution.Summary;
        return resolution.Cursor == null
            ? Empty(liveTarget, resolution.Refusal, resolution.Phase)
            : Current(liveTarget, resolution.Cursor, resolution.Refusal);
    }

    private async Task ReleaseAfterFailureAsync(ArcClaim claim, AdoptionTarget target, LegacyPlacementAdoptionPassFailureCode failureCode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var settlement = new PassSettlement
        {
            Phase = Store(Phase(claim.Cursor.Mode)), Outcome = LegacyPlacementAdoptionPassOutcome.Interrupted,
            FailureCode = failureCode, Summary = new SummaryInput { Target = target, Phase = Phase(claim.Cursor.Mode) },
            EndPosition = claim.Cursor.Position,
        };
        try { await ReleaseClaimAsync(claim, settlement, timeout.Token).ConfigureAwait(false); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Legacy adoption could not release claim {ClaimToken} after an interrupted pass; its bounded lease remains the crash-recovery fence.", claim.Token);
        }
    }

    private async Task<LegacyPlacementAdoptionSummary> RefuseActiveArcAsync(LegacyPlacementAdoptionRequest request, AdoptionTarget target,
        LegacyPlacementAdoptionCursor cursor, LegacyPlacementAdoptionRefusalValue refusal, CancellationToken cancellationToken)
    {
        var claim = await ClaimArcAsync(request.TeamId, target, cursor, cancellationToken).ConfigureAwait(false);
        if (claim.Summary != null) return claim.Summary;
        if (claim.Claim == null) return Current(target, claim.Cursor ?? cursor, claim.Refusal);
        try
        {
            return await AbortAsync(new AbortRequest
            {
                Target = target, Claim = claim.Claim, Refusal = refusal,
                Terminal = Empty(target, refusal, Phase(cursor.Mode)),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await ReleaseAfterFailureAsync(claim.Claim, target, FailureCode(exception, cancellationToken)).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<LegacyPlacementAdoptionSummary> DiscoverAsync(DiscoveryRequest request, CancellationToken cancellationToken)
    {
        EvidenceResult evidence;
        try
        {
            evidence = await EvidenceAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync(request.Lease).ConfigureAwait(false);
            throw;
        }
        if (!await DisposeAsync(request.Lease).ConfigureAwait(false))
            return await RetryClaimAsync(request.Target, request.Claim, request.Page.Rows.Count, cancellationToken).ConfigureAwait(false);

        var page = evidence.Page;
        var unresolved = page.Rows.Count - evidence.Resolved;
        var durableWitness = request.Claim.WitnessSourceWorkflowRowId ?? evidence.WitnessSourceWorkflowRowId;
        var admissible = !page.HasMore && unresolved == 0 && evidence.Retryable == 0 && !evidence.DestinationUnavailable
            && LegacyAdoptionRules.AdmitsAdoption(LegacyPlacementSurveyRefusalValue.None, evidence.Resolved, durableWitness == null ? 0 : 1);
        var refusal = evidence.DestinationUnavailable
            ? LegacyPlacementAdoptionRefusalValue.DestinationUnavailable
            : LegacyPlacementAdoptionRefusalValue.None;
        var input = new SummaryInput
        {
            Target = request.Target, Phase = LegacyPlacementAdoptionPhaseValue.Evidence, Examined = page.Rows.Count,
            Resolved = evidence.Resolved, Confirmed = evidence.Confirmed, Unresolved = unresolved,
            Counts = AdoptionCounts.Retry(evidence.Retryable), DestinationConfirmed = durableWitness != null,
            Admissible = admissible, Refusal = refusal, ReadBytes = evidence.ReadBytes,
            YieldReason = Wire(evidence.YieldReason), OversizedItem = evidence.OversizedItem,
        };

        if (evidence.Retryable > 0)
        {
            var retryInput = input with { YieldReason = LegacyPlacementAdoptionYieldReasonValue.ProviderRetryable };
            var released = await ReleaseClaimAsync(request.Claim, new PassSettlement
            {
                Phase = LegacyPlacementAdoptionArcPhase.Evidence, Outcome = LegacyPlacementAdoptionPassOutcome.Retryable,
                YieldReason = LegacyPlacementAdoptionYieldReason.ProviderRetryable,
                FailureCode = LegacyPlacementAdoptionPassFailureCode.ProviderTransient,
                Summary = retryInput, EndPosition = request.Claim.Cursor.Position,
            }, cancellationToken).ConfigureAwait(false);
            return Summary(retryInput with { NextCursor = released.Cursor?.Encode(_cursorProtector), Progress = released.Progress });
        }
        if (unresolved > 0)
            return await AbortAsync(new AbortRequest
            {
                Target = request.Target, Claim = request.Claim, Refusal = LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing,
                Examined = page.Rows.Count,
                Terminal = Summary(input with { Refusal = LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing }),
                Audit = input, AdvancesPopulation = true, PageEndPosition = page.Rows[^1].Position,
            }, cancellationToken).ConfigureAwait(false);
        if (page.HasMore)
            return await AdvanceEvidenceAsync(new EvidenceAdvance
            {
                Target = request.Target, Claim = request.Claim, Page = page,
                WitnessSourceWorkflowRowId = evidence.WitnessSourceWorkflowRowId, Summary = input,
            }, cancellationToken).ConfigureAwait(false);
        if (admissible)
            return await AdvanceEvidenceAsync(new EvidenceAdvance
            {
                Target = request.Target, Claim = request.Claim, Page = page,
                WitnessSourceWorkflowRowId = evidence.WitnessSourceWorkflowRowId, BeginMinting = true, Summary = input,
            }, cancellationToken).ConfigureAwait(false);

        return await AbortAsync(new AbortRequest
        {
            Target = request.Target, Claim = request.Claim, Refusal = LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing,
            Examined = page.Rows.Count,
            Terminal = Summary(input with { Refusal = LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing }),
            Audit = input, AdvancesPopulation = true, PageEndPosition = page.Rows[^1].Position,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LegacyPlacementAdoptionSummary> MintAsync(MintRequest request, CancellationToken cancellationToken)
    {
        MintObservationResult? observed = null;
        var witness = WitnessVerdict.Retryable;
        try
        {
            witness = await WitnessAsync(request, cancellationToken).ConfigureAwait(false);
            if (witness == WitnessVerdict.Confirmed) observed = await ObserveAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync(request.Lease).ConfigureAwait(false);
            throw;
        }
        if (!await DisposeAsync(request.Lease).ConfigureAwait(false))
            return await RetryClaimAsync(request.Target, request.Claim, request.Page.Rows.Count, cancellationToken).ConfigureAwait(false);
        if (witness != WitnessVerdict.Confirmed)
        {
            if (witness == WitnessVerdict.Missing)
                return await AbortAsync(new AbortRequest
                {
                    Target = request.Target, Claim = request.Claim, Refusal = LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing,
                    Examined = request.Page.Rows.Count,
                }, cancellationToken).ConfigureAwait(false);
            return await RetryClaimAsync(request.Target, request.Claim, request.Page.Rows.Count, cancellationToken).ConfigureAwait(false);
        }

        const bool admissible = true;
        var pageObserved = observed!;
        var page = pageObserved.Page;
        var counts = pageObserved.Counts;
        var input = new SummaryInput
        {
            Target = request.Target, Phase = LegacyPlacementAdoptionPhaseValue.Minting, Examined = page.Rows.Count,
            Resolved = pageObserved.Resolved, Confirmed = pageObserved.Confirmed, Unresolved = page.Rows.Count - pageObserved.Resolved,
            Counts = counts, DestinationConfirmed = true, Admissible = admissible, ReadBytes = pageObserved.ReadBytes,
            YieldReason = Wire(pageObserved.YieldReason), OversizedItem = pageObserved.OversizedItem,
        };
        if (counts.Retryable > 0)
        {
            var retryInput = input with { YieldReason = LegacyPlacementAdoptionYieldReasonValue.ProviderRetryable };
            var released = await ReleaseClaimAsync(request.Claim, new PassSettlement
            {
                Phase = LegacyPlacementAdoptionArcPhase.Minting, Outcome = LegacyPlacementAdoptionPassOutcome.Retryable,
                YieldReason = LegacyPlacementAdoptionYieldReason.ProviderRetryable,
                FailureCode = LegacyPlacementAdoptionPassFailureCode.ProviderTransient,
                Summary = retryInput, EndPosition = request.Claim.Cursor.Position,
            }, cancellationToken).ConfigureAwait(false);
            var cursor = released.Cursor ?? request.Claim.Cursor;
            return Summary(retryInput with { NextCursor = cursor.Encode(_cursorProtector), Progress = released.Progress });
        }
        var committed = await CommitAsync(new CommitRequest
        {
            TeamId = request.Request.TeamId, ActorId = request.Request.ActorId, Target = request.Target,
            Claim = request.Claim, Page = page, Observations = pageObserved.ToCommit, Summary = input,
        }, cancellationToken).ConfigureAwait(false);
        counts += committed.Counts;
        if (committed.TerminalSummary != null) return committed.TerminalSummary;
        return Summary(input with
        {
            Counts = counts, NextCursor = committed.Cursor?.Encode(_cursorProtector), Refusal = committed.Refusal,
            Progress = committed.Progress,
        });
    }

    private async Task<EvidenceResult> EvidenceAsync(DiscoveryRequest request, CancellationToken cancellationToken)
    {
        var resolved = 0;
        var confirmed = 0;
        var retryable = 0;
        var destinationUnavailable = false;
        LegacyRow? witness = null;
        var processed = new List<LegacyRow>(request.Page.Rows.Count);
        foreach (var row in request.Page.Rows)
        {
            if (!request.Budget.TryStart(row.SizeBytes)) break;
            processed.Add(row);
            await RenewClaimIfDueAsync(request.Claim, cancellationToken).ConfigureAwait(false);
            var key = Resolve(request.Layout, request.Configuration, row);
            if (key == null) continue;
            resolved++;
            if (destinationUnavailable)
            {
                retryable++;
                continue;
            }
            var head = await HeadAsync(request.Lease, request.Claim, key, cancellationToken).ConfigureAwait(false);
            if (head == null)
            {
                destinationUnavailable = !await DestinationAnswersAsync(request.Lease, request.Claim, cancellationToken).ConfigureAwait(false);
                retryable++;
                continue;
            }
            if (!head.IsSuccess)
            {
                var destinationLive = await DestinationAnswersAsync(request.Lease, request.Claim, cancellationToken).ConfigureAwait(false);
                if (!destinationLive) destinationUnavailable = true;
                if (!destinationLive || head.Error?.Code != ArtifactStorageErrorCode.Missing) retryable++;
                continue;
            }
            if (!Served(head, key))
            {
                retryable++;
                continue;
            }
            var candidate = new ResolvedRow(row, key, Convert.FromHexString(row.Sha256));
            var observed = await ReadAndHashAsync(new ReadHashRequest
            {
                Lease = request.Lease, Candidate = candidate, Head = head.Metadata!,
                Capabilities = request.Lease.Driver.Capabilities, Claim = request.Claim, Budget = request.Budget,
            }, cancellationToken).ConfigureAwait(false);
            if (observed == null)
            {
                retryable++;
                continue;
            }
            if (observed.State != ArtifactLocationState.Available) continue;
            confirmed++;
            if (witness == null || row.SizeBytes < witness.SizeBytes
                || row.SizeBytes == witness.SizeBytes && row.Position < witness.Position) witness = row;
        }

        var hasMore = processed.Count < request.Page.Rows.Count || request.Page.HasMore;
        request.Budget.Finish(hasMore);
        return new EvidenceResult(new LegacyPage(processed, hasMore), resolved, confirmed, retryable,
            witness?.SourceWorkflowRowId, destinationUnavailable, request.Budget.ReadBytes,
            request.Budget.YieldReason, request.Budget.OversizedItem);
    }

    private async Task<WitnessVerdict> WitnessAsync(MintRequest request, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var row = await db.LegacyPlacementAdoptionMember.AsNoTracking()
            .Where(value => value.ArcId == request.Claim.Cursor.ArcId && value.SourceWorkflowRowId == request.Claim.WitnessSourceWorkflowRowId)
            .Select(value => new LegacyRow
            {
                Position = value.Position, SourceWorkflowRowId = value.SourceWorkflowRowId, CreatedAt = value.SourceCreatedAt,
                Sha256 = value.Sha256, SizeBytes = value.SizeBytes, StorageUrl = value.StorageUrl,
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row == null) return WitnessVerdict.Missing;

        await RenewClaimIfDueAsync(request.Claim, cancellationToken).ConfigureAwait(false);
        var key = Resolve(request.Layout, request.Configuration, row);
        if (key == null) return WitnessVerdict.Missing;
        var head = await HeadAsync(request.Lease, request.Claim, key, cancellationToken).ConfigureAwait(false);
        if (head != null && Served(head, key))
        {
            var candidate = new ResolvedRow(row, key, Convert.FromHexString(row.Sha256));
            var observed = await ReadAndHashAsync(new ReadHashRequest
            {
                Lease = request.Lease, Candidate = candidate, Head = head.Metadata!,
                Capabilities = request.Lease.Driver.Capabilities, Claim = request.Claim, Budget = request.Budget,
            }, cancellationToken).ConfigureAwait(false);
            return observed?.State == ArtifactLocationState.Available ? WitnessVerdict.Confirmed
                : observed?.State == ArtifactLocationState.Corrupt ? WitnessVerdict.Missing
                : WitnessVerdict.Retryable;
        }
        if (head?.IsSuccess == true) return WitnessVerdict.Retryable;
        var destinationLive = await DestinationAnswersAsync(request.Lease, request.Claim, cancellationToken).ConfigureAwait(false);
        if (!destinationLive) return WitnessVerdict.DestinationUnavailable;
        return head?.Error?.Code == ArtifactStorageErrorCode.Missing ? WitnessVerdict.Missing : WitnessVerdict.Retryable;
    }

    private async Task<MintObservationResult> ObserveAsync(MintRequest request, CancellationToken cancellationToken)
    {
        var processed = new List<LegacyRow>(request.Page.Rows.Count);
        foreach (var row in request.Page.Rows)
        {
            if (!request.Budget.TryStart(row.SizeBytes)) break;
            processed.Add(row);
        }
        var page = new LegacyPage(processed, processed.Count < request.Page.Rows.Count || request.Page.HasMore);
        var resolved = new List<ResolvedRow>(processed.Count);
        foreach (var row in processed)
        {
            var key = Resolve(request.Layout, request.Configuration, row);
            if (key != null) resolved.Add(new ResolvedRow(row, key, Convert.FromHexString(row.Sha256)));
        }

        var counts = default(AdoptionCounts);
        var candidates = new List<ResolvedRow>(resolved.Count);
        foreach (var group in resolved.GroupBy(value => value.ObjectKey, StringComparer.Ordinal))
        {
            var first = group.First();
            var duplicates = group.Skip(1).ToList();
            if (duplicates.Any(value => !SameIdentity(first, value)))
            {
                counts += AdoptionCounts.Conflict(group.Count());
                continue;
            }

            candidates.Add(first);
        }

        var existing = await ExistingAsync(request.Request.TeamId, request.Target.RevisionId, candidates.Select(value => value.ObjectKey).ToList(), cancellationToken).ConfigureAwait(false);
        var observations = new List<LegacyObservation>();
        var confirmed = 0;
        var destinationUnanswered = false;
        foreach (var candidate in candidates)
        {
            await RenewClaimIfDueAsync(request.Claim, cancellationToken).ConfigureAwait(false);
            if (existing.TryGetValue(candidate.ObjectKey, out var recorded))
            {
                counts += Same(recorded, candidate) ? AdoptionCounts.Recorded() : AdoptionCounts.Conflict();
                continue;
            }
            if (destinationUnanswered)
            {
                counts += AdoptionCounts.Retry();
                continue;
            }

            var head = await HeadAsync(request.Lease, request.Claim, candidate.ObjectKey, cancellationToken).ConfigureAwait(false);
            if (head == null)
            {
                destinationUnanswered = !await DestinationAnswersAsync(request.Lease, request.Claim, cancellationToken).ConfigureAwait(false);
                counts += AdoptionCounts.Retry();
                continue;
            }
            if (!head.IsSuccess)
            {
                var destinationLive = await DestinationAnswersAsync(request.Lease, request.Claim, cancellationToken).ConfigureAwait(false);
                if (!destinationLive) destinationUnanswered = true;
                if (destinationLive && head.Error?.Code == ArtifactStorageErrorCode.Missing)
                    observations.Add(LegacyObservation.Missing(candidate));
                else
                    counts += AdoptionCounts.Retry();
                continue;
            }
            if (!Served(head, candidate.ObjectKey))
            {
                counts += AdoptionCounts.Retry();
                continue;
            }

            confirmed++;
            var content = await ReadAndHashAsync(new ReadHashRequest
            {
                Lease = request.Lease, Candidate = candidate, Head = head.Metadata!,
                Capabilities = request.Lease.Driver.Capabilities, Claim = request.Claim, Budget = request.Budget,
            }, cancellationToken).ConfigureAwait(false);
            if (content == null) counts += AdoptionCounts.Retry();
            else observations.Add(content);
        }

        request.Budget.Finish(page.HasMore);
        return new MintObservationResult(page, resolved.Count, confirmed, observations, counts, request.Budget.ReadBytes,
            request.Budget.YieldReason, request.Budget.OversizedItem);
    }

    private async Task<LegacyObservation?> ReadAndHashAsync(ReadHashRequest request, CancellationToken cancellationToken)
    {
        ArtifactStorageReadResult read;
        var durableEtag = request.Capabilities.HasFlag(StorageProviderCapabilities.StableETag) ? request.Head.ETag : null;
        var durableVersion = request.Capabilities.HasFlag(StorageProviderCapabilities.ObjectVersioning) ? request.Head.Version : null;
        try
        {
            await RenewClaimIfDueAsync(request.Claim, cancellationToken).ConfigureAwait(false);
            var invocation = await InvokeProviderAsync(request.Lease, request.Claim, token => request.Lease.Driver.OpenReadAsync(new ArtifactStorageReadRequest(request.Candidate.ObjectKey)
            {
                ExpectedETag = durableEtag,
                ExpectedVersion = durableVersion,
            }, token), cancellationToken).ConfigureAwait(false);
            if (!invocation.Succeeded) return null;
            read = invocation.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (LegacyProviderExceptionClassifier.Classify(exception) == LegacyProviderExceptionDisposition.Retryable)
        {
            return null;
        }

        if (!read.IsSuccess) return null;
        var metadata = read.Metadata;
        HashObservation hash;
        try
        {
            var content = read.Content ?? throw new InvalidDataException("A successful storage read returned no content stream.");
            request.Lease.Own(content);
            if (metadata == null) return null;
            var observation = await HashAsync(content, request.Candidate.Row.SizeBytes, request.Lease, request.Claim, request.Budget, cancellationToken).ConfigureAwait(false);
            if (observation == null) return null;
            hash = observation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (LegacyProviderExceptionClassifier.Classify(exception) == LegacyProviderExceptionDisposition.Retryable)
        {
            return null;
        }

        if (!string.Equals(metadata.ObjectKey, request.Candidate.ObjectKey, StringComparison.Ordinal)
            || durableEtag != null && !string.Equals(durableEtag, metadata.ETag, StringComparison.Ordinal)
            || durableVersion != null && !string.Equals(durableVersion, metadata.Version, StringComparison.Ordinal))
            return null;

        // HEAD is the existence/liveness question. Without a durable token it is not the same observation as the
        // stream and must not outvote a complete matching hash merely because the object changed between calls.
        var exact = read.ContentLength == request.Candidate.Row.SizeBytes && read.TotalLength == request.Candidate.Row.SizeBytes && metadata.Length == request.Candidate.Row.SizeBytes
            && !hash.ExceededExpected && hash.Size == request.Candidate.Row.SizeBytes && CryptographicOperations.FixedTimeEquals(hash.Digest, request.Candidate.ExpectedDigest);
        return exact
            ? LegacyObservation.Available(request.Candidate, hash, durableEtag, durableVersion)
            : LegacyObservation.Corrupt(request.Candidate, hash, durableEtag, durableVersion);
    }

    private async Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumCommitAttempts; attempt++)
        {
            try
            {
                return await CommitAttemptAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsUniqueViolation(exception) || exception is DbUpdateConcurrencyException)
            {
                if (attempt == MaximumCommitAttempts - 1) break;
            }
        }

        var retryInput = request.Summary with { Counts = AdoptionCounts.Retry(request.Observations.Count), YieldReason = LegacyPlacementAdoptionYieldReasonValue.ProviderRetryable };
        var released = await ReleaseClaimAsync(request.Claim, new PassSettlement
        {
            Phase = LegacyPlacementAdoptionArcPhase.Minting, Outcome = LegacyPlacementAdoptionPassOutcome.Retryable,
            YieldReason = LegacyPlacementAdoptionYieldReason.ProviderRetryable,
            FailureCode = LegacyPlacementAdoptionPassFailureCode.ProviderTransient,
            Summary = retryInput, EndPosition = request.Claim.Cursor.Position,
        }, cancellationToken).ConfigureAwait(false);
        if (released.Cursor != null)
            return new CommitResult(AdoptionCounts.Retry(request.Observations.Count), released.Cursor,
                LegacyPlacementAdoptionRefusalValue.None, null, released.Progress);

        await using var db = CreateDb();
        var arc = await db.LegacyPlacementAdoptionArc.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == request.Claim.Cursor.ArcId, cancellationToken).ConfigureAwait(false);
        return arc != null && IsTerminal(arc.State)
            ? new CommitResult(default, null, LegacyPlacementAdoptionRefusalValue.None, StoredSummary(arc))
            : new CommitResult(AdoptionCounts.Retry(request.Observations.Count), request.Claim.Cursor,
                LegacyPlacementAdoptionRefusalValue.CursorSuperseded, null);
    }

    private async Task<CommitResult> CommitAttemptAsync(CommitRequest request, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await StorageProfileHeadLock.TakeAsync(db.Database, request.Target.ProfileId, cancellationToken).ConfigureAwait(false);
        var arc = await LockedArcAsync(db, request.Claim.Cursor.ArcId, cancellationToken).ConfigureAwait(false);
        if (!Owns(arc, request.Claim))
        {
            if (arc != null && IsTerminal(arc.State))
                return new CommitResult(default, null, LegacyPlacementAdoptionRefusalValue.None, StoredSummary(arc));
            return new CommitResult(default, arc == null ? request.Claim.Cursor : CursorFor(arc),
                LegacyPlacementAdoptionRefusalValue.CursorSuperseded, null);
        }

        var profile = await db.StorageProfile.AsNoTracking()
            .Where(value => value.TeamId == request.TeamId && value.Id == request.Target.ProfileId)
            .Select(value => new { value.State, value.CurrentRevision })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (profile == null || profile.State == StorageProfileState.Retired || profile.CurrentRevision != request.Target.Revision)
        {
            var terminal = Summary(request.Summary with { Refusal = LegacyPlacementAdoptionRefusalValue.CursorStale });
            var result = await BeginCommitCleaningAsync(db, arc!, new CommitCleaning
            {
                TerminalState = LegacyPlacementAdoptionArcState.Stale, Terminal = terminal,
                Refusal = LegacyPlacementAdoptionRefusalValue.CursorStale, Claim = request.Claim, Audit = request.Summary,
            }, cancellationToken).ConfigureAwait(false);
            await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        var counts = default(AdoptionCounts);
        var witness = await db.LegacyPlacementAdoptionMember.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ArcId == arc!.Id && value.SourceWorkflowRowId == arc.WitnessSourceWorkflowRowId,
                cancellationToken).ConfigureAwait(false);
        if (witness == null || request.Claim.WitnessSourceWorkflowRowId != witness.SourceWorkflowRowId)
        {
            var terminal = Summary(request.Summary with
            {
                Counts = request.Summary.Counts + AdoptionCounts.Conflict(),
                Refusal = LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing,
            });
            var result = await BeginCommitCleaningAsync(db, arc!, new CommitCleaning
            {
                TerminalState = LegacyPlacementAdoptionArcState.Stale, Terminal = terminal,
                Refusal = LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing, Claim = request.Claim, Audit = request.Summary,
            }, cancellationToken).ConfigureAwait(false);
            await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        var witnessRow = Row(witness);
        var sourceWorkflowRowIds = request.Page.Rows.Select(value => value.SourceWorkflowRowId)
            .Append(witnessRow.SourceWorkflowRowId).Distinct().Order().ToArray();

        // Retention's destructive transaction locks its declaration first, removes physical bytes, then deletes the
        // immutable source row. Take that same first lock in SHARE mode before revalidating the source: if retention
        // won, this waits until the row and bytes are both gone; if adoption won, retention cannot remove bytes until
        // the Available sidecar and its provenance event commit. Reversing these two locks deadlocks the real reaper.
        await db.WorkflowArtifactRetention.FromSqlInterpolated($$"""
            SELECT retention.*, retention.xmin FROM workflow_artifact_retention retention
            WHERE retention.artifact_id = ANY ({{sourceWorkflowRowIds}})
            ORDER BY retention.artifact_id
            FOR SHARE
            """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        var sourceRows = await db.WorkflowArtifact.FromSqlInterpolated($$"""
            SELECT artifact.* FROM workflow_artifact artifact
            WHERE artifact.team_id = {{request.TeamId}} AND artifact.id = ANY ({{sourceWorkflowRowIds}})
            ORDER BY artifact.id
            FOR KEY SHARE
            """).AsNoTracking().ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);
        if (!SourceIsCurrent(witnessRow, sourceRows))
        {
            var terminal = Summary(request.Summary with
            {
                Counts = request.Summary.Counts + AdoptionCounts.Conflict(),
                Refusal = LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing,
            });
            var result = await BeginCommitCleaningAsync(db, arc!, new CommitCleaning
            {
                TerminalState = LegacyPlacementAdoptionArcState.Stale, Terminal = terminal,
                Refusal = LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing, Claim = request.Claim, Audit = request.Summary,
            }, cancellationToken).ConfigureAwait(false);
            await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        var observations = request.Observations.Where(value => SourceIsCurrent(value.Candidate.Row, sourceRows)).ToList();
        counts += AdoptionCounts.Conflict(request.Observations.Count - observations.Count);

        var keys = observations.Select(value => value.Candidate.ObjectKey).Distinct(StringComparer.Ordinal).ToList();
        var existingLocations = keys.Count == 0
            ? new Dictionary<string, ArtifactLocation>(StringComparer.Ordinal)
            : await db.ArtifactLocation.AsNoTracking().Include(value => value.ArtifactObject)
                .Where(value => value.TeamId == request.TeamId && value.StorageProfileRevisionId == request.Target.RevisionId && keys.Contains(value.ObjectKey))
                .ToDictionaryAsync(value => value.ObjectKey, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var digests = observations.Select(value => value.Candidate.ExpectedDigest).ToArray();
        var artifacts = digests.Length == 0
            ? []
            : await db.ArtifactObject
                .Where(value => value.TeamId == request.TeamId && value.DigestAlgorithm == ArtifactDigestAlgorithm.Sha256 && digests.Contains(value.Digest))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var objectsByDigest = artifacts.ToDictionary(value => Convert.ToHexString(value.Digest), StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (existingLocations.TryGetValue(observation.Candidate.ObjectKey, out var existing))
            {
                counts += Same(existing, observation.Candidate) ? AdoptionCounts.Recorded() : AdoptionCounts.Conflict();
                continue;
            }

            var digestKey = Convert.ToHexString(observation.Candidate.ExpectedDigest);
            objectsByDigest.TryGetValue(digestKey, out var artifact);
            if (artifact != null && artifact.SizeBytes != observation.Candidate.Row.SizeBytes)
            {
                counts += AdoptionCounts.Conflict();
                continue;
            }
            if (artifact == null)
            {
                artifact = new ArtifactObject
                {
                    Id = Guid.NewGuid(), TeamId = request.TeamId, DigestAlgorithm = ArtifactDigestAlgorithm.Sha256,
                    Digest = observation.Candidate.ExpectedDigest, SizeBytes = observation.Candidate.Row.SizeBytes,
                    CreatedDate = now, CreatedBy = request.ActorId,
                };
                db.ArtifactObject.Add(artifact);
                objectsByDigest.Add(digestKey, artifact);
            }

            var location = Location(request, observation, artifact.Id, now);
            db.ArtifactLocation.Add(location);
            db.ArtifactLocationEvent.Add(Event(location, request.ActorId, observation.Candidate.Row.SourceWorkflowRowId));
            existingLocations.Add(observation.Candidate.ObjectKey, location);
            counts += observation.State switch
            {
                ArtifactLocationState.Available => AdoptionCounts.AvailableOne(),
                ArtifactLocationState.Missing => AdoptionCounts.MissingOne(),
                ArtifactLocationState.Corrupt => AdoptionCounts.CorruptOne(),
                _ => default,
            };
        }

        var pageCounts = request.Summary.Counts + counts;
        arc!.CurrentPosition = request.Page.Rows[^1].Position;
        var pagePositions = request.Page.Rows.Select(value => value.Position).ToHashSet();
        if (request.Page.HasMore)
        {
            pagePositions.Remove(witness.Position);
            SettlePass(db, arc, request.Claim, new PassSettlement
            {
                Phase = LegacyPlacementAdoptionArcPhase.Minting, Outcome = LegacyPlacementAdoptionPassOutcome.Advanced,
                YieldReason = Store(request.Summary.YieldReason), FailureCode = LegacyPlacementAdoptionPassFailureCode.None,
                Summary = request.Summary with { Counts = pageCounts }, EndPosition = request.Page.Rows[^1].Position,
                AdvancesPopulation = true, CompletedAt = now,
            });
            Release(arc, now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await db.LegacyPlacementAdoptionMember
                .Where(value => value.ArcId == arc.Id && pagePositions.Contains(value.Position))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CommitResult(counts, CursorFor(arc), LegacyPlacementAdoptionRefusalValue.None, null, Progress(arc));
        }

        pagePositions.Add(witness.Position);
        SettlePass(db, arc, request.Claim, new PassSettlement
        {
            Phase = LegacyPlacementAdoptionArcPhase.Minting, Outcome = LegacyPlacementAdoptionPassOutcome.Advanced,
            YieldReason = Store(request.Summary.YieldReason), FailureCode = LegacyPlacementAdoptionPassFailureCode.None,
            Summary = request.Summary with { Counts = pageCounts }, EndPosition = request.Page.Rows[^1].Position,
            AdvancesPopulation = true, CompletedAt = now,
        });
        var completed = SummaryWithProgress(request.Summary with { Counts = pageCounts }, arc);
        Complete(arc, LegacyPlacementAdoptionArcState.Completed, completed, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await db.LegacyPlacementAdoptionMember
            .Where(value => value.ArcId == arc.Id && pagePositions.Contains(value.Position))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (await db.LegacyPlacementAdoptionMember.AnyAsync(value => value.ArcId == arc.Id, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"Legacy adoption arc {arc.Id} reached its final page with unexpected membership behind its durable cursor.");
        await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CommitResult(counts, null, LegacyPlacementAdoptionRefusalValue.None, completed);
    }

    private async Task<CommitResult> BeginCommitCleaningAsync(CodeSpaceDbContext db, LegacyPlacementAdoptionArc arc,
        CommitCleaning request, CancellationToken cancellationToken)
    {
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        SettlePass(db, arc, request.Claim, new PassSettlement
        {
            Phase = Store(request.Audit.Phase), Outcome = LegacyPlacementAdoptionPassOutcome.Aborted,
            FailureCode = FailureCode(request.Refusal), Summary = request.Audit,
            EndPosition = request.Claim.Cursor.Position, CompletedAt = now,
        });
        var terminal = request.Terminal with { Progress = Progress(arc) };
        BeginCleaning(arc, request.TerminalState, terminal, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await DeleteMemberPageAsync(db, arc.Id, LegacyPlacementAdoptionLimits.MaxRowsPerPass, cancellationToken).ConfigureAwait(false);
        if (!await db.LegacyPlacementAdoptionMember.AnyAsync(value => value.ArcId == arc.Id, cancellationToken).ConfigureAwait(false))
            Complete(arc, request.TerminalState, terminal, await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return IsTerminal(arc.State)
            ? new CommitResult(default, null, LegacyPlacementAdoptionRefusalValue.None, StoredSummary(arc))
            : new CommitResult(default, CursorFor(arc), request.Refusal, null);
    }

    private static ArtifactLocation Location(CommitRequest request, LegacyObservation observation, Guid artifactId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TeamId = request.TeamId, ArtifactObjectId = artifactId, StorageProfileRevisionId = request.Target.RevisionId,
        Locator = observation.Candidate.ObjectKey, ObjectKey = observation.Candidate.ObjectKey,
        ProviderObjectVersion = observation.ProviderObjectVersion, ProviderETag = observation.ProviderETag,
        ProviderChecksumAlgorithm = observation.ObservedDigest == null ? null : "Sha256", ProviderChecksum = observation.ObservedDigest,
        ObservedSizeBytes = observation.ObservedSize, State = observation.State, Revision = 1, VerifiedAt = now,
        LastErrorCode = observation.ErrorCode, LastErrorMessage = observation.ErrorMessage,
        CreatedDate = now, CreatedBy = request.ActorId, LastModifiedDate = now, LastModifiedBy = request.ActorId,
    };

    private static ArtifactLocationEvent Event(ArtifactLocation location, Guid actorId, Guid sourceWorkflowRowId) => new()
    {
        Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = 1,
        EventType = location.State == ArtifactLocationState.Available ? ArtifactLocationEventType.Verified : ArtifactLocationEventType.Observed,
        State = location.State, ObservedAt = location.VerifiedAt!.Value, ProviderObjectVersion = location.ProviderObjectVersion,
        ProviderETag = location.ProviderETag, ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm,
        ProviderChecksum = location.ProviderChecksum, ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
        ErrorCode = location.LastErrorCode, ErrorMessage = location.LastErrorMessage,
        DetailsJson = JsonSerializer.Serialize(new
        {
            source = "legacy-placement-adoption/v1",
            workflow_artifact_id = sourceWorkflowRowId,
        }),
        CreatedBy = actorId,
    };

    private async Task<ArcResolution> ResolveArcAsync(LegacyPlacementAdoptionRequest request, AdoptionTarget target, CancellationToken cancellationToken)
    {
        if (request.Cursor != null)
        {
            if (!LegacyPlacementAdoptionCursor.TryDecode(request.Cursor, request.ProfileId, _cursorProtector, out var decoded))
                return new ArcResolution(null, null, LegacyPlacementAdoptionRefusalValue.CursorInvalid, LegacyPlacementAdoptionPhaseValue.Evidence, false);

            await using var read = CreateDb();
            var stored = await read.LegacyPlacementAdoptionArc.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == decoded.ArcId && value.TeamId == request.TeamId
                    && value.StorageProfileId == request.ProfileId, cancellationToken).ConfigureAwait(false);
            if (stored == null) return new ArcResolution(null, null, LegacyPlacementAdoptionRefusalValue.CursorStale, Phase(decoded.Mode), false);
            if (decoded.ProfileRevision != stored.ProfileRevision)
                return new ArcResolution(null, null, LegacyPlacementAdoptionRefusalValue.CursorInvalid, Phase(decoded.Mode), false);
            if (IsTerminal(stored.State)) return new ArcResolution(null, StoredSummary(stored), LegacyPlacementAdoptionRefusalValue.None, Phase(decoded.Mode), false);
            if (stored.ProfileRevision != target.Revision || target.State == StorageProfileState.Retired)
                return await CloseStaleArcAsync(request, stored.Id,
                    target.State == StorageProfileState.Retired ? LegacyPlacementAdoptionRefusalValue.ProfileRetired
                        : LegacyPlacementAdoptionRefusalValue.CursorStale, cancellationToken).ConfigureAwait(false);
            return new ArcResolution(decoded, null, LegacyPlacementAdoptionRefusalValue.None, Phase(decoded.Mode), false);
        }

        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await TakeTeamArcLockAsync(db, request.TeamId, cancellationToken).ConfigureAwait(false);
        var terminalCutoff = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false) - TerminalRetention;
        await db.LegacyPlacementAdoptionArc.Where(value => (value.State == LegacyPlacementAdoptionArcState.Completed
                || value.State == LegacyPlacementAdoptionArcState.Expired || value.State == LegacyPlacementAdoptionArcState.Stale)
            && value.CompletedAt < terminalCutoff)
            .OrderBy(value => value.CompletedAt).ThenBy(value => value.Id).Take(TerminalCleanupBatch)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var live = await db.LegacyPlacementAdoptionArc.FromSqlInterpolated($$"""
            SELECT arc.*, arc.xmin FROM legacy_placement_adoption_arc arc
            WHERE arc.team_id = {{request.TeamId}} AND arc.state IN ('Active', 'Cleaning')
            FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (live != null)
        {
            var liveNow = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
            if (live.State == LegacyPlacementAdoptionArcState.Cleaning || live.ExpiresAt <= liveNow)
            {
                var liveTarget = await StoredTargetAsync(db, live, cancellationToken).ConfigureAwait(false);
                var resolution = await CloseLockedArcAsync(db, live, new ArcClosure
                {
                    Target = liveTarget, Refusal = LegacyPlacementAdoptionRefusalValue.CursorStale,
                    TerminalState = live.State == LegacyPlacementAdoptionArcState.Cleaning
                        ? live.TerminalState!.Value : LegacyPlacementAdoptionArcState.Expired,
                }, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return live.StorageProfileId == request.ProfileId
                    ? resolution
                    : new ArcResolution(null, null, LegacyPlacementAdoptionRefusalValue.ArcAlreadyActive,
                        LegacyPlacementAdoptionPhaseValue.Cleaning, false);
            }
            if (live.StorageProfileId == request.ProfileId
                && (live.ProfileRevision != target.Revision || target.State == StorageProfileState.Retired))
            {
                var refusal = target.State == StorageProfileState.Retired
                    ? LegacyPlacementAdoptionRefusalValue.ProfileRetired : LegacyPlacementAdoptionRefusalValue.CursorStale;
                var liveTarget = await StoredTargetAsync(db, live, cancellationToken).ConfigureAwait(false);
                var resolution = await CloseLockedArcAsync(db, live, new ArcClosure
                {
                    Target = liveTarget, Refusal = refusal, TerminalState = LegacyPlacementAdoptionArcState.Stale,
                }, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return resolution;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return live.StorageProfileId == request.ProfileId && live.ProfileRevision == target.Revision
                ? new ArcResolution(CursorFor(live), null, LegacyPlacementAdoptionRefusalValue.CursorSuperseded, Phase(live.Phase), true)
                : new ArcResolution(null, null, LegacyPlacementAdoptionRefusalValue.ArcAlreadyActive, Phase(live.Phase), false);
        }

        if (target.State == StorageProfileState.Retired)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ArcResolution(null, null, LegacyPlacementAdoptionRefusalValue.ProfileRetired,
                LegacyPlacementAdoptionPhaseValue.Evidence, false);
        }

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        var arc = new LegacyPlacementAdoptionArc
        {
            Id = Guid.NewGuid(), TeamId = request.TeamId, StorageProfileId = request.ProfileId,
            StorageProfileRevisionId = target.RevisionId, ProfileRevision = target.Revision, CreatedBy = request.ActorId,
            Phase = LegacyPlacementAdoptionArcPhase.Evidence, State = LegacyPlacementAdoptionArcState.Active,
            CurrentPosition = 0, MemberCount = 0, Revision = 1, AuditVersion = 1, CreatedAt = now, LastModifiedAt = now,
            ExpiresAt = now + ArcTtl,
        };
        db.LegacyPlacementAdoptionArc.Add(arc);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO legacy_placement_adoption_member
                (arc_id, source_workflow_row_id, source_created_at, sha256, size_bytes, storage_url)
            SELECT {{arc.Id}}, artifact.id, artifact.created_at, artifact.sha256, artifact.size_bytes, artifact.storage_url
            FROM workflow_artifact artifact
            WHERE artifact.team_id = {{request.TeamId}} AND artifact.storage_url IS NOT NULL
            ORDER BY artifact.created_at, artifact.id
            """, cancellationToken).ConfigureAwait(false);
        arc.MemberCount = await db.LegacyPlacementAdoptionMember.CountAsync(value => value.ArcId == arc.Id, cancellationToken).ConfigureAwait(false);
        arc.SealedAt = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        arc.LastModifiedAt = arc.SealedAt.Value;
        arc.Revision++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (arc.MemberCount == 0)
        {
            var final = Empty(target, LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing) with { Progress = Progress(arc) };
            BeginCleaning(arc, LegacyPlacementAdoptionArcState.Completed, final, arc.SealedAt.Value);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Complete(arc, LegacyPlacementAdoptionArcState.Completed, final,
                await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return IsTerminal(arc.State)
            ? new ArcResolution(null, StoredSummary(arc), LegacyPlacementAdoptionRefusalValue.None, LegacyPlacementAdoptionPhaseValue.Evidence, false)
            : new ArcResolution(CursorFor(arc), null, LegacyPlacementAdoptionRefusalValue.None, LegacyPlacementAdoptionPhaseValue.Evidence, false);
    }

    private async Task<ArcResolution> CloseStaleArcAsync(LegacyPlacementAdoptionRequest request, Guid arcId,
        LegacyPlacementAdoptionRefusalValue refusal, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var arc = await LockedArcAsync(db, arcId, cancellationToken).ConfigureAwait(false);
        if (arc == null || arc.TeamId != request.TeamId)
            return new ArcResolution(null, null, LegacyPlacementAdoptionRefusalValue.CursorStale, LegacyPlacementAdoptionPhaseValue.Evidence, false);
        var target = await StoredTargetAsync(db, arc, cancellationToken).ConfigureAwait(false);
        var resolution = await CloseLockedArcAsync(db, arc, new ArcClosure
        {
            Target = target, Refusal = refusal, TerminalState = LegacyPlacementAdoptionArcState.Stale,
        }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return resolution;
    }

    private async Task<ArcResolution> CloseLockedArcAsync(CodeSpaceDbContext db, LegacyPlacementAdoptionArc arc,
        ArcClosure closure, CancellationToken cancellationToken)
    {
        if (IsTerminal(arc.State))
            return new ArcResolution(null, StoredSummary(arc), LegacyPlacementAdoptionRefusalValue.None, Phase(arc.Phase), false);

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (arc.ClaimToken != null && arc.ClaimExpiresAt > now)
            return new ArcResolution(CursorFor(arc), null, LegacyPlacementAdoptionRefusalValue.ArcBusy, Phase(arc.Phase), true);

        LegacyPlacementAdoptionSummary terminal;
        if (arc.State == LegacyPlacementAdoptionArcState.Cleaning)
        {
            terminal = StoredSummary(arc);
            if (arc.ClaimToken != null) Release(arc, now);
        }
        else
        {
            terminal = Empty(closure.Target, closure.Refusal, Phase(arc.Phase)) with { Progress = Progress(arc) };
            BeginCleaning(arc, closure.TerminalState, terminal, now);
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await DeleteMemberPageAsync(db, arc.Id, LegacyPlacementAdoptionLimits.MaxRowsPerPass, cancellationToken).ConfigureAwait(false);
        if (!await db.LegacyPlacementAdoptionMember.AnyAsync(value => value.ArcId == arc.Id, cancellationToken).ConfigureAwait(false))
            Complete(arc, arc.TerminalState!.Value, terminal,
                await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return IsTerminal(arc.State)
            ? new ArcResolution(null, StoredSummary(arc), LegacyPlacementAdoptionRefusalValue.None, LegacyPlacementAdoptionPhaseValue.Cleaning, false)
            : new ArcResolution(CursorFor(arc), null, closure.Refusal, LegacyPlacementAdoptionPhaseValue.Cleaning, true);
    }

    private static async Task<AdoptionTarget> StoredTargetAsync(CodeSpaceDbContext db, LegacyPlacementAdoptionArc arc, CancellationToken cancellationToken)
    {
        var state = await db.StorageProfile.AsNoTracking()
            .Where(value => value.TeamId == arc.TeamId && value.Id == arc.StorageProfileId)
            .Select(value => value.State).SingleAsync(cancellationToken).ConfigureAwait(false);
        var revision = await db.StorageProfileRevision.AsNoTracking()
            .Where(value => value.TeamId == arc.TeamId && value.Id == arc.StorageProfileRevisionId)
            .Select(value => new { value.ProviderTypeKey, value.NonSecretConfigJson })
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        return new AdoptionTarget
        {
            ProfileId = arc.StorageProfileId, State = state, RevisionId = arc.StorageProfileRevisionId,
            Revision = arc.ProfileRevision, ProviderTypeKey = revision.ProviderTypeKey,
            NonSecretConfigJson = revision.NonSecretConfigJson,
        };
    }

    private async Task<LegacyPage> PageAsync(LegacyPlacementAdoptionCursor cursor, int take, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var rows = await db.LegacyPlacementAdoptionMember.AsNoTracking()
            .Where(value => value.ArcId == cursor.ArcId && value.Position > cursor.Position)
            .OrderBy(value => value.Position).Take(take + 1)
            .Select(value => new LegacyRow
            {
                Position = value.Position, SourceWorkflowRowId = value.SourceWorkflowRowId, CreatedAt = value.SourceCreatedAt,
                Sha256 = value.Sha256, SizeBytes = value.SizeBytes, StorageUrl = value.StorageUrl,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        return new LegacyPage(hasMore ? rows.GetRange(0, take) : rows, hasMore);
    }

    private async Task<ClaimResolution> ClaimArcAsync(Guid teamId, AdoptionTarget target, LegacyPlacementAdoptionCursor cursor, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var arc = await LockedArcAsync(db, cursor.ArcId, cancellationToken).ConfigureAwait(false);
        if (arc == null || arc.TeamId != teamId) return new ClaimResolution(null, null, null, LegacyPlacementAdoptionRefusalValue.CursorStale);
        if (IsTerminal(arc.State)) return new ClaimResolution(null, null, StoredSummary(arc), LegacyPlacementAdoptionRefusalValue.None);

        var current = CursorFor(arc);
        if (arc.StorageProfileId != target.ProfileId || arc.ProfileRevision != target.Revision)
            return new ClaimResolution(null, current, null, LegacyPlacementAdoptionRefusalValue.CursorStale);
        if (cursor.ArcRevision != arc.Revision || cursor.Mode != current.Mode || cursor.Position != current.Position)
            return new ClaimResolution(null, current, null, LegacyPlacementAdoptionRefusalValue.CursorSuperseded);

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (arc.ClaimToken != null && arc.ClaimExpiresAt > now)
            return new ClaimResolution(null, current, null, LegacyPlacementAdoptionRefusalValue.ArcBusy);
        if (arc.ExpiresAt <= now)
        {
            var final = Empty(target, LegacyPlacementAdoptionRefusalValue.CursorStale, Phase(arc.Phase)) with { Progress = Progress(arc) };
            BeginCleaning(arc, LegacyPlacementAdoptionArcState.Expired, final, now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ClaimResolution(null, CursorFor(arc), null, LegacyPlacementAdoptionRefusalValue.CursorSuperseded);
        }

        var token = Guid.NewGuid();
        arc.ClaimToken = token;
        arc.ClaimStartedAt = now;
        arc.ClaimExpiresAt = now + _runtime.ClaimTtl;
        arc.ExpiresAt = now + ArcTtl;
        arc.LastModifiedAt = now;
        arc.Revision++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ClaimResolution(new ArcClaim(CursorFor(arc), token, arc.WitnessSourceWorkflowRowId,
            now, _runtime.Clock.GetUtcNow() + _runtime.ClaimRenewalInterval), null, null, LegacyPlacementAdoptionRefusalValue.None);
    }

    private async Task RenewClaimIfDueAsync(ArcClaim claim, CancellationToken cancellationToken)
    {
        if (_runtime.Clock.GetUtcNow() < claim.RenewAfter) return;
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var arc = await LockedArcAsync(db, claim.Cursor.ArcId, cancellationToken).ConfigureAwait(false);
        if (!Owns(arc, claim)) throw new InvalidOperationException($"Legacy adoption claim {claim.Token} was lost before its pass settled.");
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (arc!.ClaimExpiresAt <= now) throw new InvalidOperationException($"Legacy adoption claim {claim.Token} expired before renewal.");
        arc.ClaimExpiresAt = now + _runtime.ClaimTtl;
        arc.ExpiresAt = now + ArcTtl;
        arc.LastModifiedAt = now;
        arc.Revision++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        claim.Renew(CursorFor(arc), _runtime.Clock.GetUtcNow() + _runtime.ClaimRenewalInterval);
    }

    private async Task<LegacyPlacementAdoptionSummary> AdvanceEvidenceAsync(EvidenceAdvance request, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await StorageProfileHeadLock.TakeAsync(db.Database, request.Target.ProfileId, cancellationToken).ConfigureAwait(false);
        var arc = await LockedArcAsync(db, request.Claim.Cursor.ArcId, cancellationToken).ConfigureAwait(false);
        if (!Owns(arc, request.Claim))
            return Current(request.Target, arc == null ? request.Claim.Cursor : CursorFor(arc), LegacyPlacementAdoptionRefusalValue.CursorSuperseded);

        var profile = await db.StorageProfile.AsNoTracking()
            .Where(value => value.TeamId == arc!.TeamId && value.Id == request.Target.ProfileId)
            .Select(value => new { value.State, value.CurrentRevision })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (profile == null || profile.State == StorageProfileState.Retired || profile.CurrentRevision != request.Target.Revision)
        {
            var terminal = Summary(request.Summary with { Refusal = LegacyPlacementAdoptionRefusalValue.CursorStale });
            var cleaned = await BeginCommitCleaningAsync(db, arc!, new CommitCleaning
            {
                TerminalState = LegacyPlacementAdoptionArcState.Stale, Terminal = terminal,
                Refusal = LegacyPlacementAdoptionRefusalValue.CursorStale, Claim = request.Claim, Audit = request.Summary,
            }, cancellationToken).ConfigureAwait(false);
            await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
            return cleaned.TerminalSummary ?? Current(request.Target, cleaned.Cursor!, cleaned.Refusal);
        }

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (request.WitnessSourceWorkflowRowId != null)
        {
            var candidates = arc!.WitnessSourceWorkflowRowId == null
                ? [request.WitnessSourceWorkflowRowId.Value]
                : new[] { arc.WitnessSourceWorkflowRowId.Value, request.WitnessSourceWorkflowRowId.Value };
            arc.WitnessSourceWorkflowRowId = await db.LegacyPlacementAdoptionMember.AsNoTracking()
                .Where(value => value.ArcId == arc.Id && candidates.Contains(value.SourceWorkflowRowId))
                .OrderBy(value => value.SizeBytes).ThenBy(value => value.Position)
                .Select(value => value.SourceWorkflowRowId).FirstAsync(cancellationToken).ConfigureAwait(false);
        }
        if (request.BeginMinting)
        {
            arc!.Phase = LegacyPlacementAdoptionArcPhase.Minting;
            if (arc.WitnessSourceWorkflowRowId == null)
                throw new InvalidOperationException($"Legacy adoption arc {arc.Id} cannot mint without a confirmed durable witness.");
            arc.CurrentPosition = 0;
        }
        else
        {
            arc!.CurrentPosition = request.Page.Rows[^1].Position;
        }
        SettlePass(db, arc, request.Claim, new PassSettlement
        {
            Phase = LegacyPlacementAdoptionArcPhase.Evidence, Outcome = LegacyPlacementAdoptionPassOutcome.Advanced,
            YieldReason = Store(request.Summary.YieldReason), FailureCode = LegacyPlacementAdoptionPassFailureCode.None,
            Summary = request.Summary, EndPosition = request.Page.Rows[^1].Position,
            AdvancesPopulation = true, CompletedAt = now,
        });
        Release(arc, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SummaryWithProgress(request.Summary, arc, CursorFor(arc).Encode(_cursorProtector));
    }

    private async Task<LegacyPlacementAdoptionSummary> RetryClaimAsync(AdoptionTarget target, ArcClaim claim, int examined, CancellationToken cancellationToken)
    {
        var input = new SummaryInput
        {
            Target = target, Phase = Phase(claim.Cursor.Mode), Examined = examined, Unresolved = examined,
            Counts = AdoptionCounts.Retry(examined),
            Refusal = LegacyPlacementAdoptionRefusalValue.DestinationUnavailable,
            YieldReason = LegacyPlacementAdoptionYieldReasonValue.ProviderRetryable,
        };
        var released = await ReleaseClaimAsync(claim, new PassSettlement
        {
            Phase = Store(Phase(claim.Cursor.Mode)), Outcome = LegacyPlacementAdoptionPassOutcome.Retryable,
            YieldReason = LegacyPlacementAdoptionYieldReason.ProviderRetryable,
            FailureCode = LegacyPlacementAdoptionPassFailureCode.ProviderTransient,
            Summary = input, EndPosition = claim.Cursor.Position,
        }, cancellationToken).ConfigureAwait(false);
        var cursor = released.Cursor ?? claim.Cursor;
        return Summary(input with { NextCursor = cursor.Encode(_cursorProtector), Progress = released.Progress });
    }

    private async Task<ClaimRelease> ReleaseClaimAsync(ArcClaim claim, PassSettlement? settlement, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var arc = await LockedArcAsync(db, claim.Cursor.ArcId, cancellationToken).ConfigureAwait(false);
        if (!Owns(arc, claim)) return new ClaimRelease(arc == null || IsTerminal(arc.State) ? null : CursorFor(arc), arc == null ? null : Progress(arc));
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (settlement != null) SettlePass(db, arc!, claim, settlement with { CompletedAt = now });
        Release(arc!, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ClaimRelease(CursorFor(arc!), Progress(arc!));
    }

    private async Task<LegacyPlacementAdoptionSummary> AbortAsync(AbortRequest request, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var arc = await LockedArcAsync(db, request.Claim.Cursor.ArcId, cancellationToken).ConfigureAwait(false);
        if (!Owns(arc, request.Claim))
            return Current(request.Target, arc == null ? request.Claim.Cursor : CursorFor(arc), LegacyPlacementAdoptionRefusalValue.CursorSuperseded);

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        var audit = request.Audit ?? new SummaryInput
        {
            Target = request.Target, Phase = Phase(request.Claim.Cursor.Mode), Examined = request.Examined,
            Unresolved = request.Examined, Refusal = request.Refusal,
        };
        SettlePass(db, arc!, request.Claim, new PassSettlement
        {
            Phase = Store(Phase(request.Claim.Cursor.Mode)), Outcome = LegacyPlacementAdoptionPassOutcome.Aborted,
            FailureCode = FailureCode(request.Refusal), Summary = audit, EndPosition = request.AdvancesPopulation && request.PageEndPosition != null
                ? request.PageEndPosition.Value : request.Claim.Cursor.Position,
            AdvancesPopulation = request.AdvancesPopulation, CompletedAt = now,
        });
        var terminal = (request.Terminal ?? Summary(audit)) with { Progress = Progress(arc!) };
        var owned = arc!;
        BeginCleaning(owned, request.Refusal == LegacyPlacementAdoptionRefusalValue.CursorStale
            ? LegacyPlacementAdoptionArcState.Stale : LegacyPlacementAdoptionArcState.Completed, terminal, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await DeleteMemberPageAsync(db, owned.Id, LegacyPlacementAdoptionLimits.MaxRowsPerPass, cancellationToken).ConfigureAwait(false);
        if (!await db.LegacyPlacementAdoptionMember.AnyAsync(value => value.ArcId == owned.Id, cancellationToken).ConfigureAwait(false))
            Complete(owned, owned.TerminalState!.Value, terminal, await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return IsTerminal(owned.State) ? StoredSummary(owned) : Current(request.Target, CursorFor(owned), request.Refusal);
    }

    private async Task<LegacyPlacementAdoptionSummary> CleanAsync(LegacyPlacementAdoptionRequest request, AdoptionTarget target,
        LegacyPlacementAdoptionCursor cursor, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var arc = await LockedArcAsync(db, cursor.ArcId, cancellationToken).ConfigureAwait(false);
        if (arc == null || IsTerminal(arc.State))
            return arc == null ? Current(target, cursor, LegacyPlacementAdoptionRefusalValue.CursorStale) : StoredSummary(arc);
        var current = CursorFor(arc);
        if (arc.State != LegacyPlacementAdoptionArcState.Cleaning || arc.ClaimToken != null
            || cursor.ArcRevision != current.ArcRevision || cursor.Mode != current.Mode || cursor.Position != current.Position)
            return Current(target, current, LegacyPlacementAdoptionRefusalValue.CursorSuperseded);

        var deleted = await DeleteMemberPageAsync(db, arc.Id,
            Math.Clamp(request.BatchSize, 1, LegacyPlacementAdoptionLimits.MaxRowsPerPass), cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (!await db.LegacyPlacementAdoptionMember.AnyAsync(value => value.ArcId == arc.Id, cancellationToken).ConfigureAwait(false))
            Complete(arc, arc.TerminalState!.Value, StoredSummary(arc), now);
        else
        {
            arc.LastModifiedAt = now;
            arc.ExpiresAt = now + ArcTtl;
            arc.Revision++;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return IsTerminal(arc.State) ? StoredSummary(arc) : Summary(new SummaryInput
        {
            Target = target, Phase = LegacyPlacementAdoptionPhaseValue.Cleaning, Examined = deleted,
            NextCursor = CursorFor(arc).Encode(_cursorProtector), Refusal = LegacyPlacementAdoptionRefusalValue.None,
            Progress = Progress(arc),
        });
    }

    private async Task<LegacyPlacementAdoptionSummary> FinishEmptyPageAsync(LegacyPlacementAdoptionRequest request, AdoptionTarget target,
        ArcClaim claim, CancellationToken cancellationToken)
    {
        var refusal = claim.Cursor.Mode == LegacyPlacementAdoptionCursorMode.Evidence
            ? LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing : LegacyPlacementAdoptionRefusalValue.None;
        var terminal = Empty(target, refusal, Phase(claim.Cursor.Mode));
        return await AbortAsync(new AbortRequest
        {
            Target = target, Claim = claim, Refusal = refusal, Terminal = terminal,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> DeleteMemberPageAsync(CodeSpaceDbContext db, Guid arcId, int take, CancellationToken cancellationToken)
    {
        var positions = await db.LegacyPlacementAdoptionMember.Where(value => value.ArcId == arcId)
            .OrderBy(value => value.Position).Select(value => value.Position).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
        return positions.Count == 0 ? 0 : await db.LegacyPlacementAdoptionMember
            .Where(value => value.ArcId == arcId && positions.Contains(value.Position)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<LegacyPlacementAdoptionArc?> LockedArcAsync(CodeSpaceDbContext db, Guid arcId, CancellationToken cancellationToken) =>
        db.LegacyPlacementAdoptionArc.FromSqlInterpolated(
            $"SELECT arc.*, arc.xmin FROM legacy_placement_adoption_arc arc WHERE arc.id = {arcId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private static async Task TakeTeamArcLockAsync(CodeSpaceDbContext db, Guid teamId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({teamId.ToString()}, 186))", cancellationToken).ConfigureAwait(false);

    private static bool Owns(LegacyPlacementAdoptionArc? arc, ArcClaim claim) => arc != null && !IsTerminal(arc.State)
        && arc.ClaimToken == claim.Token && arc.Revision == claim.Cursor.ArcRevision;

    private static void Release(LegacyPlacementAdoptionArc arc, DateTimeOffset now)
    {
        arc.ClaimToken = null;
        arc.ClaimStartedAt = null;
        arc.ClaimExpiresAt = null;
        arc.LastModifiedAt = now;
        arc.ExpiresAt = now + ArcTtl;
        arc.Revision++;
    }

    private static void BeginCleaning(LegacyPlacementAdoptionArc arc, LegacyPlacementAdoptionArcState terminalState,
        LegacyPlacementAdoptionSummary final, DateTimeOffset now)
    {
        arc.Phase = LegacyPlacementAdoptionArcPhase.Cleaning;
        arc.State = LegacyPlacementAdoptionArcState.Cleaning;
        arc.TerminalState = terminalState;
        arc.FinalSummaryJson = JsonSerializer.Serialize(final);
        Release(arc, now);
    }

    private static void Complete(LegacyPlacementAdoptionArc arc, LegacyPlacementAdoptionArcState terminalState,
        LegacyPlacementAdoptionSummary final, DateTimeOffset now)
    {
        arc.State = terminalState;
        arc.TerminalState = terminalState;
        arc.ClaimToken = null;
        arc.ClaimStartedAt = null;
        arc.ClaimExpiresAt = null;
        arc.CompletedAt = now;
        arc.LastModifiedAt = now;
        arc.FinalSummaryJson = JsonSerializer.Serialize(final);
        arc.Revision++;
    }

    private static void SettlePass(CodeSpaceDbContext db, LegacyPlacementAdoptionArc arc, ArcClaim claim, PassSettlement settlement)
    {
        if (arc.AuditVersion == 0) return;
        var evidenceAdvance = settlement.AdvancesPopulation && settlement.Phase == LegacyPlacementAdoptionArcPhase.Evidence;
        var mintAdvance = settlement.AdvancesPopulation && settlement.Phase == LegacyPlacementAdoptionArcPhase.Minting;
        var counts = settlement.Summary.Counts;
        var audit = new LegacyPlacementAdoptionPassAudit
        {
            ArcId = arc.Id, ClaimToken = claim.Token, Phase = settlement.Phase, Outcome = settlement.Outcome,
            YieldReason = settlement.YieldReason, FailureCode = settlement.FailureCode,
            StartPosition = claim.Cursor.Position, EndPosition = settlement.EndPosition,
            Examined = settlement.Summary.Examined, Resolved = settlement.Summary.Resolved,
            Confirmed = settlement.Summary.Confirmed,
            EvidenceExaminedDelta = evidenceAdvance ? settlement.Summary.Examined : 0,
            EvidenceResolvedDelta = evidenceAdvance ? settlement.Summary.Resolved : 0,
            EvidenceConfirmedDelta = evidenceAdvance ? settlement.Summary.Confirmed : 0,
            MintExaminedDelta = mintAdvance ? settlement.Summary.Examined : 0,
            AvailableDelta = mintAdvance ? counts.Available : 0,
            MissingDelta = mintAdvance ? counts.Missing : 0,
            CorruptDelta = mintAdvance ? counts.Corrupt : 0,
            AlreadyRecordedDelta = mintAdvance ? counts.AlreadyRecorded : 0,
            ConflictsDelta = mintAdvance ? counts.Conflicts : 0,
            RetryableDelta = counts.Retryable,
            ReadBytesDelta = settlement.Summary.ReadBytes, OversizedItem = settlement.Summary.OversizedItem,
            StartedAt = claim.StartedAt, CompletedAt = settlement.CompletedAt,
        };
        db.LegacyPlacementAdoptionPassAudit.Add(audit);
        arc.EvidenceExamined += audit.EvidenceExaminedDelta;
        arc.EvidenceResolved += audit.EvidenceResolvedDelta;
        arc.EvidenceConfirmed += audit.EvidenceConfirmedDelta;
        arc.MintExamined += audit.MintExaminedDelta;
        arc.Available += audit.AvailableDelta;
        arc.Missing += audit.MissingDelta;
        arc.Corrupt += audit.CorruptDelta;
        arc.AlreadyRecorded += audit.AlreadyRecordedDelta;
        arc.Conflicts += audit.ConflictsDelta;
        arc.Retryable += audit.RetryableDelta;
        arc.ReadBytes += audit.ReadBytesDelta;
        arc.CompletedPasses++;
        arc.LastSettledClaimToken = claim.Token;
        if (audit.YieldReason is LegacyPlacementAdoptionYieldReason.ByteBudget or LegacyPlacementAdoptionYieldReason.TimeBudget)
            arc.BudgetYields++;
        if (audit.OversizedItem) arc.OversizedPasses++;
    }

    private static LegacyPlacementAdoptionSummary SummaryWithProgress(SummaryInput input, LegacyPlacementAdoptionArc arc,
        string? nextCursor = null) => Summary(input with { Progress = Progress(arc), NextCursor = nextCursor });

    private static bool IsTerminal(LegacyPlacementAdoptionArcState state) => state is LegacyPlacementAdoptionArcState.Completed
        or LegacyPlacementAdoptionArcState.Expired or LegacyPlacementAdoptionArcState.Stale;

    private LegacyPlacementAdoptionCursor CursorFor(LegacyPlacementAdoptionArc arc) => new()
    {
        ProfileId = arc.StorageProfileId, ProfileRevision = arc.ProfileRevision, ArcId = arc.Id, ArcRevision = arc.Revision,
        Mode = arc.State == LegacyPlacementAdoptionArcState.Cleaning ? LegacyPlacementAdoptionCursorMode.Cleaning : arc.Phase switch
        {
            LegacyPlacementAdoptionArcPhase.Minting => LegacyPlacementAdoptionCursorMode.Minting,
            LegacyPlacementAdoptionArcPhase.Cleaning => LegacyPlacementAdoptionCursorMode.Cleaning,
            _ => LegacyPlacementAdoptionCursorMode.Evidence,
        },
        Position = arc.CurrentPosition,
    };

    private static LegacyPlacementAdoptionSummary StoredSummary(LegacyPlacementAdoptionArc arc) =>
        JsonSerializer.Deserialize<LegacyPlacementAdoptionSummary>(arc.FinalSummaryJson
            ?? throw new InvalidOperationException($"Legacy adoption arc {arc.Id} has no terminal summary."))
        ?? throw new InvalidOperationException($"Legacy adoption arc {arc.Id} has an unreadable terminal summary.");

    private LegacyPlacementAdoptionSummary Current(AdoptionTarget target, LegacyPlacementAdoptionCursor cursor, LegacyPlacementAdoptionRefusalValue refusal) =>
        Summary(new SummaryInput
        {
            Target = target, Phase = Phase(cursor.Mode), NextCursor = cursor.Encode(_cursorProtector), Refusal = refusal,
        });

    private async Task<AdoptionTarget?> TargetAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        return await (from profile in db.StorageProfile.AsNoTracking()
            join revision in db.StorageProfileRevision.AsNoTracking()
                on new { profile.TeamId, StorageProfileId = profile.Id, Revision = profile.CurrentRevision }
                equals new { revision.TeamId, revision.StorageProfileId, revision.Revision }
            where profile.TeamId == teamId && profile.Id == profileId
            select new AdoptionTarget
            {
                ProfileId = profile.Id, State = profile.State, RevisionId = revision.Id, Revision = revision.Revision,
                ProviderTypeKey = revision.ProviderTypeKey, NonSecretConfigJson = revision.NonSecretConfigJson,
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, ExistingPlacement>> ExistingAsync(Guid teamId, Guid revisionId, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0) return new Dictionary<string, ExistingPlacement>(StringComparer.Ordinal);
        await using var db = CreateDb();
        var rows = await db.ArtifactLocation.AsNoTracking()
            .Where(value => value.TeamId == teamId && value.StorageProfileRevisionId == revisionId && keys.Contains(value.ObjectKey))
            .Select(value => new ExistingPlacement(value.ObjectKey, value.Locator, value.ArtifactObject.Digest, value.ArtifactObject.SizeBytes))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(value => value.ObjectKey, StringComparer.Ordinal);
    }

    private async Task<bool> DisposeAsync(StorageRuntimeDriverLease lease)
    {
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (LegacyProviderExceptionClassifier.Classify(exception) == LegacyProviderExceptionDisposition.Retryable)
        {
            _logger.LogWarning("Legacy placement adoption could not cleanly release its storage driver; provider detail was discarded.");
            return false;
        }
        catch (Exception exception) when (LegacyProviderExceptionClassifier.Classify(exception) == LegacyProviderExceptionDisposition.Rejected)
        {
            throw new LegacyProviderRejectedException();
        }
    }

    private async Task<ArtifactStorageHeadResult?> HeadAsync(StorageRuntimeDriverLease lease, ArcClaim claim, string objectKey, CancellationToken cancellationToken)
    {
        var invocation = await InvokeProviderAsync(lease, claim,
            token => lease.Driver.HeadAsync(new ArtifactStorageHeadRequest(objectKey), token), cancellationToken).ConfigureAwait(false);
        return invocation.Succeeded ? invocation.Value : null;
    }

    private async Task<bool> DestinationAnswersAsync(StorageRuntimeDriverLease lease, ArcClaim claim, CancellationToken cancellationToken)
    {
        var invocation = await InvokeProviderAsync(lease, claim,
            token => lease.Driver.ProbeAsync(new ArtifactStorageProbeRequest(), token), cancellationToken).ConfigureAwait(false);
        return invocation.Succeeded && invocation.Value.Status is ArtifactStorageProbeStatus.Available or ArtifactStorageProbeStatus.ReadOnly;
    }

    private async Task<HashObservation?> HashAsync(Stream content, long expectedSize, StorageRuntimeDriverLease lease,
        ArcClaim claim, LegacyPlacementPassBudget budget, CancellationToken cancellationToken)
    {
        if (expectedSize < 0) throw new ArgumentOutOfRangeException(nameof(expectedSize));
        // A timed-out plugin may finish a read after this pass returns. A dedicated buffer prevents that late write
        // from corrupting unrelated work; the lease keeps the stream alive until the tracked operation settles.
        var buffer = new byte[HashBufferSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var size = 0L;
        while (size < expectedSize)
        {
            var requested = (int)Math.Min(HashBufferSize, expectedSize - size);
            var invocation = await InvokeProviderAsync(lease, claim,
                token => content.ReadAsync(buffer.AsMemory(0, requested), token), cancellationToken).ConfigureAwait(false);
            if (!invocation.Succeeded) return null;
            var read = invocation.Value;
            if (read == 0) return new HashObservation(hash.GetHashAndReset(), size, ExceededExpected: false);
            size = checked(size + read);
            budget.AddReadBytes(read);
            hash.AppendData(buffer, 0, read);
        }

        var extraRead = await InvokeProviderAsync(lease, claim,
            token => content.ReadAsync(buffer.AsMemory(0, 1), token), cancellationToken).ConfigureAwait(false);
        if (!extraRead.Succeeded) return null;
        if (extraRead.Value != 0) budget.AddReadBytes(extraRead.Value);
        return new HashObservation(hash.GetHashAndReset(), size, ExceededExpected: extraRead.Value != 0);
    }

    private async Task<ProviderInvocation<T>> InvokeProviderAsync<T>(StorageRuntimeDriverLease lease, ArcClaim claim,
        Func<CancellationToken, ValueTask<T>> invoke, CancellationToken cancellationToken)
    {
        await RenewClaimIfDueAsync(claim, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_runtime.ProviderOperationTimeout);
        Task<T>? pending = null;
        var abandoned = false;
        try
        {
            pending = lease.Track(invoke(timeout.Token).AsTask());
            return ProviderInvocation<T>.Success(await pending.WaitAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (pending != null) { abandoned = true; lease.Abandon(pending); }
            return ProviderInvocation<T>.Retry();
        }
        catch (OperationCanceledException)
        {
            if (pending != null) { abandoned = true; lease.Abandon(pending); }
            throw;
        }
        catch (Exception exception)
        {
            var disposition = LegacyProviderExceptionClassifier.Classify(exception);
            if (disposition == LegacyProviderExceptionDisposition.Retryable) return ProviderInvocation<T>.Retry();
            if (disposition == LegacyProviderExceptionDisposition.Rejected) throw new LegacyProviderRejectedException();
            throw;
        }
        finally
        {
            if (pending != null && !abandoned) lease.Release(pending);
        }
    }

    private async Task<StorageRuntimeDriverResolution?> OpenDriverAsync(StorageRuntimeDriverRequest request, ArcClaim claim,
        CancellationToken cancellationToken)
    {
        await RenewClaimIfDueAsync(claim, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_runtime.ProviderOperationTimeout);
        var pending = _runtime.Broker.OpenAsync(request, timeout.Token).AsTask();
        try
        {
            return await pending.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _ = DisposeLateResolutionAsync(pending);
            return null;
        }
        catch (OperationCanceledException)
        {
            _ = DisposeLateResolutionAsync(pending);
            throw;
        }
        catch (Exception exception)
        {
            var disposition = LegacyProviderExceptionClassifier.Classify(exception);
            if (disposition == LegacyProviderExceptionDisposition.Retryable) return null;
            if (disposition == LegacyProviderExceptionDisposition.Rejected) throw new LegacyProviderRejectedException();
            throw;
        }
    }

    private static async Task DisposeLateResolutionAsync(Task<StorageRuntimeDriverResolution> pending)
    {
        try
        {
            if (await pending.ConfigureAwait(false) is StorageRuntimeDriverResolution.Ready ready)
                await ready.Lease.DisposeAsync().ConfigureAwait(false);
        }
        catch { /* Late provider detail is intentionally discarded after the bounded caller has left. */ }
    }

    private static string? Resolve(IStorageProviderLegacyLayout layout, JsonElement configuration, LegacyRow row)
    {
        try { return layout.ResolveLegacyObjectKey(configuration, row.Sha256, row.StorageUrl); }
        catch (Exception exception) when (exception is FormatException or UriFormatException or ArgumentException) { return null; }
    }

    private static bool Same(ExistingPlacement existing, ResolvedRow candidate) =>
        string.Equals(existing.ObjectKey, candidate.ObjectKey, StringComparison.Ordinal)
        && string.Equals(existing.Locator, candidate.ObjectKey, StringComparison.Ordinal)
        && existing.SizeBytes == candidate.Row.SizeBytes
        && CryptographicOperations.FixedTimeEquals(existing.Digest, candidate.ExpectedDigest);

    private static bool SameIdentity(ResolvedRow left, ResolvedRow right) =>
        left.Row.SizeBytes == right.Row.SizeBytes && CryptographicOperations.FixedTimeEquals(left.ExpectedDigest, right.ExpectedDigest);

    private static bool SourceIsCurrent(LegacyRow expected, IReadOnlyDictionary<Guid, WorkflowArtifact> sources) =>
        sources.TryGetValue(expected.SourceWorkflowRowId, out var current)
        && current.CreatedAt == expected.CreatedAt
        && current.SizeBytes == expected.SizeBytes
        && string.Equals(current.Sha256, expected.Sha256, StringComparison.Ordinal)
        && string.Equals(current.StorageUrl, expected.StorageUrl, StringComparison.Ordinal);

    private static LegacyRow Row(LegacyPlacementAdoptionMember member) => new()
    {
        Position = member.Position, SourceWorkflowRowId = member.SourceWorkflowRowId, CreatedAt = member.SourceCreatedAt,
        Sha256 = member.Sha256, SizeBytes = member.SizeBytes, StorageUrl = member.StorageUrl,
    };

    private static bool Same(ArtifactLocation existing, ResolvedRow candidate) =>
        string.Equals(existing.ObjectKey, candidate.ObjectKey, StringComparison.Ordinal)
        && string.Equals(existing.Locator, candidate.ObjectKey, StringComparison.Ordinal)
        && existing.ArtifactObject.SizeBytes == candidate.Row.SizeBytes
        && CryptographicOperations.FixedTimeEquals(existing.ArtifactObject.Digest, candidate.ExpectedDigest);

    private static bool Served(ArtifactStorageHeadResult head, string objectKey) =>
        head.IsSuccess && head.Metadata != null && string.Equals(head.Metadata.ObjectKey, objectKey, StringComparison.Ordinal);

    private static JsonElement Configuration(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static LegacyPlacementAdoptionPhaseValue Phase(LegacyPlacementAdoptionCursorMode mode) => mode switch
    {
        LegacyPlacementAdoptionCursorMode.Evidence => LegacyPlacementAdoptionPhaseValue.Evidence,
        LegacyPlacementAdoptionCursorMode.Minting => LegacyPlacementAdoptionPhaseValue.Minting,
        _ => LegacyPlacementAdoptionPhaseValue.Cleaning,
    };

    private static LegacyPlacementAdoptionPhaseValue Phase(LegacyPlacementAdoptionArcPhase phase) => phase switch
    {
        LegacyPlacementAdoptionArcPhase.Evidence => LegacyPlacementAdoptionPhaseValue.Evidence,
        LegacyPlacementAdoptionArcPhase.Minting => LegacyPlacementAdoptionPhaseValue.Minting,
        _ => LegacyPlacementAdoptionPhaseValue.Cleaning,
    };

    private static LegacyPlacementAdoptionSummary Empty(Guid profileId, LegacyPlacementAdoptionRefusalValue refusal) => new()
    {
        ProfileId = profileId, Phase = LegacyPlacementAdoptionPhaseValue.Evidence, Refusal = refusal,
        Examined = 0, Resolved = 0, Confirmed = 0, Unresolved = 0, Available = 0, Missing = 0, Corrupt = 0,
        AlreadyRecorded = 0, Conflicts = 0, Retryable = 0, DestinationConfirmed = false, AdoptionAdmissible = false,
    };

    private static LegacyPlacementAdoptionSummary Empty(AdoptionTarget target, LegacyPlacementAdoptionRefusalValue refusal,
        LegacyPlacementAdoptionPhaseValue phase = LegacyPlacementAdoptionPhaseValue.Evidence) => Empty(target.ProfileId, refusal) with
    {
        ProviderTypeKey = target.ProviderTypeKey, ProfileRevision = target.Revision, Phase = phase,
    };

    private static LegacyPlacementAdoptionSummary Summary(SummaryInput input) => new()
    {
        ProfileId = input.Target.ProfileId, ProviderTypeKey = input.Target.ProviderTypeKey, ProfileRevision = input.Target.Revision, Phase = input.Phase,
        Examined = input.Examined, Resolved = input.Resolved, Confirmed = input.Confirmed, Unresolved = input.Unresolved,
        Available = input.Counts.Available, Missing = input.Counts.Missing, Corrupt = input.Counts.Corrupt,
        AlreadyRecorded = input.Counts.AlreadyRecorded, Conflicts = input.Counts.Conflicts, Retryable = input.Counts.Retryable,
        DestinationConfirmed = input.DestinationConfirmed, AdoptionAdmissible = input.Admissible,
        NextCursor = input.NextCursor, Refusal = input.Refusal, ReadBytes = input.ReadBytes,
        YieldReason = input.YieldReason, OversizedItem = input.OversizedItem, Progress = input.Progress,
    };

    private static LegacyPlacementAdoptionProgress? Progress(LegacyPlacementAdoptionArc arc) => arc.AuditVersion == 0 ? null : new()
    {
        MemberCount = arc.MemberCount, EvidenceExamined = arc.EvidenceExamined, EvidenceResolved = arc.EvidenceResolved,
        EvidenceConfirmed = arc.EvidenceConfirmed, MintExamined = arc.MintExamined, Available = arc.Available,
        Missing = arc.Missing, Corrupt = arc.Corrupt, AlreadyRecorded = arc.AlreadyRecorded, Conflicts = arc.Conflicts,
        Retryable = arc.Retryable, ReadBytes = arc.ReadBytes, CompletedPasses = arc.CompletedPasses,
        BudgetYields = arc.BudgetYields, OversizedPasses = arc.OversizedPasses,
    };

    private static LegacyPlacementAdoptionYieldReasonValue Wire(LegacyPlacementAdoptionYieldReason reason) => reason switch
    {
        LegacyPlacementAdoptionYieldReason.RowLimit => LegacyPlacementAdoptionYieldReasonValue.RowLimit,
        LegacyPlacementAdoptionYieldReason.ByteBudget => LegacyPlacementAdoptionYieldReasonValue.ByteBudget,
        LegacyPlacementAdoptionYieldReason.TimeBudget => LegacyPlacementAdoptionYieldReasonValue.TimeBudget,
        LegacyPlacementAdoptionYieldReason.ProviderRetryable => LegacyPlacementAdoptionYieldReasonValue.ProviderRetryable,
        _ => LegacyPlacementAdoptionYieldReasonValue.None,
    };

    private static LegacyPlacementAdoptionYieldReason Store(LegacyPlacementAdoptionYieldReasonValue reason) => reason switch
    {
        LegacyPlacementAdoptionYieldReasonValue.RowLimit => LegacyPlacementAdoptionYieldReason.RowLimit,
        LegacyPlacementAdoptionYieldReasonValue.ByteBudget => LegacyPlacementAdoptionYieldReason.ByteBudget,
        LegacyPlacementAdoptionYieldReasonValue.TimeBudget => LegacyPlacementAdoptionYieldReason.TimeBudget,
        LegacyPlacementAdoptionYieldReasonValue.ProviderRetryable => LegacyPlacementAdoptionYieldReason.ProviderRetryable,
        _ => LegacyPlacementAdoptionYieldReason.None,
    };

    private static LegacyPlacementAdoptionArcPhase Store(LegacyPlacementAdoptionPhaseValue phase) => phase switch
    {
        LegacyPlacementAdoptionPhaseValue.Minting => LegacyPlacementAdoptionArcPhase.Minting,
        LegacyPlacementAdoptionPhaseValue.Cleaning => LegacyPlacementAdoptionArcPhase.Cleaning,
        _ => LegacyPlacementAdoptionArcPhase.Evidence,
    };

    private static LegacyPlacementAdoptionPassFailureCode FailureCode(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            return LegacyPlacementAdoptionPassFailureCode.CallerCancelled;
        return LegacyPlacementAdoptionPassFailureCode.ProgrammingFault;
    }

    private static LegacyPlacementAdoptionPassFailureCode FailureCode(LegacyPlacementAdoptionRefusalValue refusal) => refusal switch
    {
        LegacyPlacementAdoptionRefusalValue.CursorStale or LegacyPlacementAdoptionRefusalValue.CursorInvalid
            or LegacyPlacementAdoptionRefusalValue.CursorSuperseded => LegacyPlacementAdoptionPassFailureCode.CursorStale,
        LegacyPlacementAdoptionRefusalValue.DestinationUnavailable => LegacyPlacementAdoptionPassFailureCode.ProviderTransient,
        LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing => LegacyPlacementAdoptionPassFailureCode.AdmissionEvidenceMissing,
        LegacyPlacementAdoptionRefusalValue.ProviderRejected => LegacyPlacementAdoptionPassFailureCode.ProviderRejected,
        LegacyPlacementAdoptionRefusalValue.ProviderHasNoLegacyLayout or LegacyPlacementAdoptionRefusalValue.ProviderHasNoStreamingRead
            or LegacyPlacementAdoptionRefusalValue.ProviderHasNoHealthProbe or LegacyPlacementAdoptionRefusalValue.ProfileMissing
            or LegacyPlacementAdoptionRefusalValue.ProfileRetired => LegacyPlacementAdoptionPassFailureCode.ProviderRejected,
        _ => LegacyPlacementAdoptionPassFailureCode.None,
    };

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);
    private static Task<DateTimeOffset> DatabaseClockAsync(CodeSpaceDbContext db, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
    private static bool IsUniqueViolation(Exception exception) => exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } }
        || exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private sealed record AdoptionTarget
    {
        public required Guid ProfileId { get; init; }
        public required StorageProfileState State { get; init; }
        public required Guid RevisionId { get; init; }
        public required int Revision { get; init; }
        public required string ProviderTypeKey { get; init; }
        public required string NonSecretConfigJson { get; init; }
    }

    private sealed record SummaryInput
    {
        public required AdoptionTarget Target { get; init; }
        public required LegacyPlacementAdoptionPhaseValue Phase { get; init; }
        public int Examined { get; init; }
        public int Resolved { get; init; }
        public int Confirmed { get; init; }
        public int Unresolved { get; init; }
        public AdoptionCounts Counts { get; init; }
        public bool DestinationConfirmed { get; init; }
        public bool Admissible { get; init; }
        public string? NextCursor { get; init; }
        public LegacyPlacementAdoptionRefusalValue Refusal { get; init; }
        public long ReadBytes { get; init; }
        public LegacyPlacementAdoptionYieldReasonValue YieldReason { get; init; }
        public bool OversizedItem { get; init; }
        public LegacyPlacementAdoptionProgress? Progress { get; init; }
    }

    private sealed class LegacyRow
    {
        public required long Position { get; init; }
        public required Guid SourceWorkflowRowId { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required string Sha256 { get; init; }
        public required long SizeBytes { get; init; }
        public required string StorageUrl { get; init; }
    }
    private sealed record LegacyPage(List<LegacyRow> Rows, bool HasMore);
    private sealed record ResolvedRow(LegacyRow Row, string ObjectKey, byte[] ExpectedDigest);
    private sealed record ExistingPlacement(string ObjectKey, string Locator, byte[] Digest, long SizeBytes);
    private sealed record EvidenceResult(LegacyPage Page, int Resolved, int Confirmed, int Retryable,
        Guid? WitnessSourceWorkflowRowId, bool DestinationUnavailable, long ReadBytes,
        LegacyPlacementAdoptionYieldReason YieldReason, bool OversizedItem);
    private sealed record HashObservation(byte[] Digest, long Size, bool ExceededExpected);
    private sealed record ArcResolution(LegacyPlacementAdoptionCursor? Cursor, LegacyPlacementAdoptionSummary? Summary,
        LegacyPlacementAdoptionRefusalValue Refusal, LegacyPlacementAdoptionPhaseValue Phase, bool ResumeOnly);
    private sealed class ArcClaim
    {
        public ArcClaim(LegacyPlacementAdoptionCursor cursor, Guid token, Guid? witnessSourceWorkflowRowId,
            DateTimeOffset startedAt, DateTimeOffset renewAfter)
        {
            Cursor = cursor;
            Token = token;
            WitnessSourceWorkflowRowId = witnessSourceWorkflowRowId;
            StartedAt = startedAt;
            RenewAfter = renewAfter;
        }

        public LegacyPlacementAdoptionCursor Cursor { get; private set; }
        public Guid Token { get; }
        public Guid? WitnessSourceWorkflowRowId { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset RenewAfter { get; private set; }

        public void Renew(LegacyPlacementAdoptionCursor cursor, DateTimeOffset renewAfter)
        {
            Cursor = cursor;
            RenewAfter = renewAfter;
        }
    }
    private sealed record ClaimResolution(ArcClaim? Claim, LegacyPlacementAdoptionCursor? Cursor,
        LegacyPlacementAdoptionSummary? Summary, LegacyPlacementAdoptionRefusalValue Refusal);
    private sealed class DiscoveryRequest
    {
        public required LegacyPlacementAdoptionRequest Request { get; init; }
        public required AdoptionTarget Target { get; init; }
        public required ArcClaim Claim { get; init; }
        public required LegacyPage Page { get; init; }
        public required IStorageProviderLegacyLayout Layout { get; init; }
        public required JsonElement Configuration { get; init; }
        public required StorageRuntimeDriverLease Lease { get; init; }
        public required LegacyPlacementPassBudget Budget { get; init; }
    }

    private sealed class MintRequest
    {
        public required LegacyPlacementAdoptionRequest Request { get; init; }
        public required AdoptionTarget Target { get; init; }
        public required ArcClaim Claim { get; init; }
        public required LegacyPage Page { get; init; }
        public required IStorageProviderLegacyLayout Layout { get; init; }
        public required JsonElement Configuration { get; init; }
        public required StorageRuntimeDriverLease Lease { get; init; }
        public required LegacyPlacementPassBudget Budget { get; init; }
    }

    private sealed class EvidenceAdvance
    {
        public required AdoptionTarget Target { get; init; }
        public required ArcClaim Claim { get; init; }
        public required LegacyPage Page { get; init; }
        public Guid? WitnessSourceWorkflowRowId { get; init; }
        public bool BeginMinting { get; init; }
        public required SummaryInput Summary { get; init; }
    }

    private sealed class AbortRequest
    {
        public required AdoptionTarget Target { get; init; }
        public required ArcClaim Claim { get; init; }
        public required LegacyPlacementAdoptionRefusalValue Refusal { get; init; }
        public int Examined { get; init; }
        public LegacyPlacementAdoptionSummary? Terminal { get; init; }
        public SummaryInput? Audit { get; init; }
        public bool AdvancesPopulation { get; init; }
        public long? PageEndPosition { get; init; }
    }

    private sealed record MintObservationResult(LegacyPage Page, int Resolved, int Confirmed,
        IReadOnlyList<LegacyObservation> ToCommit, AdoptionCounts Counts, long ReadBytes,
        LegacyPlacementAdoptionYieldReason YieldReason, bool OversizedItem);
    private sealed class CommitRequest
    {
        public required Guid TeamId { get; init; }
        public required Guid ActorId { get; init; }
        public required AdoptionTarget Target { get; init; }
        public required ArcClaim Claim { get; init; }
        public required LegacyPage Page { get; init; }
        public required IReadOnlyList<LegacyObservation> Observations { get; init; }
        public required SummaryInput Summary { get; init; }
    }
    private sealed class ReadHashRequest
    {
        public required StorageRuntimeDriverLease Lease { get; init; }
        public required ResolvedRow Candidate { get; init; }
        public required ArtifactStorageObjectMetadata Head { get; init; }
        public required StorageProviderCapabilities Capabilities { get; init; }
        public required ArcClaim Claim { get; init; }
        public required LegacyPlacementPassBudget Budget { get; init; }
    }
    private sealed class CommitCleaning
    {
        public required LegacyPlacementAdoptionArcState TerminalState { get; init; }
        public required LegacyPlacementAdoptionSummary Terminal { get; init; }
        public required LegacyPlacementAdoptionRefusalValue Refusal { get; init; }
        public required ArcClaim Claim { get; init; }
        public required SummaryInput Audit { get; init; }
    }
    private sealed record PassSettlement
    {
        public required LegacyPlacementAdoptionArcPhase Phase { get; init; }
        public required LegacyPlacementAdoptionPassOutcome Outcome { get; init; }
        public LegacyPlacementAdoptionYieldReason YieldReason { get; init; }
        public LegacyPlacementAdoptionPassFailureCode FailureCode { get; init; }
        public required SummaryInput Summary { get; init; }
        public required long EndPosition { get; init; }
        public bool AdvancesPopulation { get; init; }
        public DateTimeOffset CompletedAt { get; init; }
    }
    private sealed class ArcClosure
    {
        public required AdoptionTarget Target { get; init; }
        public required LegacyPlacementAdoptionRefusalValue Refusal { get; init; }
        public required LegacyPlacementAdoptionArcState TerminalState { get; init; }
    }
    private sealed record CommitResult(AdoptionCounts Counts, LegacyPlacementAdoptionCursor? Cursor,
        LegacyPlacementAdoptionRefusalValue Refusal, LegacyPlacementAdoptionSummary? TerminalSummary,
        LegacyPlacementAdoptionProgress? Progress = null);
    private sealed record ClaimRelease(LegacyPlacementAdoptionCursor? Cursor, LegacyPlacementAdoptionProgress? Progress);
    private sealed record ProviderInvocation<T>(bool Succeeded, T Value)
    {
        public static ProviderInvocation<T> Success(T value) => new(true, value);
        public static ProviderInvocation<T> Retry() => new(false, default!);
    }

    private enum WitnessVerdict
    {
        Confirmed,
        Missing,
        Retryable,
        DestinationUnavailable,
    }

    private sealed record LegacyObservation
    {
        public required ResolvedRow Candidate { get; init; }
        public required ArtifactLocationState State { get; init; }
        public byte[]? ObservedDigest { get; init; }
        public long? ObservedSize { get; init; }
        public string? ProviderETag { get; init; }
        public string? ProviderObjectVersion { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }

        public static LegacyObservation Available(ResolvedRow row, HashObservation hash, string? etag, string? version) =>
            new()
            {
                Candidate = row, State = ArtifactLocationState.Available, ObservedDigest = hash.Digest,
                ObservedSize = hash.Size, ProviderETag = etag, ProviderObjectVersion = version,
            };
        public static LegacyObservation Missing(ResolvedRow row) =>
            new()
            {
                Candidate = row, State = ArtifactLocationState.Missing, ErrorCode = "LegacyObjectMissing",
                ErrorMessage = "The confirmed legacy destination no longer holds this object key.",
            };
        public static LegacyObservation Corrupt(ResolvedRow row, HashObservation hash, string? etag, string? version) =>
            new()
            {
                Candidate = row, State = ArtifactLocationState.Corrupt,
                ObservedDigest = hash.ExceededExpected ? null : hash.Digest, ObservedSize = hash.ExceededExpected ? null : hash.Size,
                ProviderETag = etag, ProviderObjectVersion = version,
                ErrorCode = hash.ExceededExpected ? "LegacyObjectExceedsClaimedSize" : "LegacyIntegrityMismatch",
                ErrorMessage = hash.ExceededExpected
                    ? "The legacy object exceeded the immutable artifact size; reading stopped after one extra byte."
                    : "The legacy object's streamed bytes do not match the immutable artifact identity.",
            };
    }

    private readonly record struct AdoptionCounts
    {
        public int Available { get; init; }
        public int Missing { get; init; }
        public int Corrupt { get; init; }
        public int AlreadyRecorded { get; init; }
        public int Conflicts { get; init; }
        public int Retryable { get; init; }

        public static AdoptionCounts AvailableOne() => new() { Available = 1 };
        public static AdoptionCounts MissingOne() => new() { Missing = 1 };
        public static AdoptionCounts CorruptOne() => new() { Corrupt = 1 };
        public static AdoptionCounts Recorded(int count = 1) => new() { AlreadyRecorded = count };
        public static AdoptionCounts Conflict(int count = 1) => new() { Conflicts = count };
        public static AdoptionCounts Retry(int count = 1) => new() { Retryable = count };
        public static AdoptionCounts operator +(AdoptionCounts left, AdoptionCounts right) => new()
        {
            Available = left.Available + right.Available, Missing = left.Missing + right.Missing,
            Corrupt = left.Corrupt + right.Corrupt, AlreadyRecorded = left.AlreadyRecorded + right.AlreadyRecorded,
            Conflicts = left.Conflicts + right.Conflicts, Retryable = left.Retryable + right.Retryable,
        };
    }
}
