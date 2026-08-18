using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof that the tool-call plane's invariants cannot be forged. Almost every assertion here is a
/// COUNTER-EXAMPLE: the illegal row is offered and the database refuses it, because an invariant that only holds while
/// every writer remembers it is not an invariant. Nothing reads or writes these tables in production yet, so these
/// teeth are the entire contract a later capture slice will build on.
///
/// <para>Three invariants are declared and each is pinned from BOTH directions: attempt ordinals contiguous from one
/// (which is why deleting a single try is refused — the head is derived and no delete walks it back), exactly one
/// attempt in flight per call, and a terminal call whose attempts are all terminal and none of them of unknown
/// outcome, so a call that may or may not have landed its effect cannot read as clean. Alongside them the
/// redaction discipline is pinned as schema — referenced bytes must name the pass that cleared them, and absent
/// content can never be claimed as exact content — because a tool argument can carry a credential.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunToolCallPersistenceTests
{
    private const string RedactionPolicy = "run-secret-redactor/v1";
    private readonly PostgresFixture _fixture;

    public WorkflowRunToolCallPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Appending_attempts_advances_the_call_head_and_a_finished_call_completes()
    {
        var world = await SeedRunAsync();
        var call = await SeedCallAsync(world);

        await SeedAttemptAsync(world, call, ordinal: 1);

        using (var scope = _fixture.BeginScope())
        {
            var stored = await Calls(scope).SingleAsync(candidate => candidate.Id == call.Id);
            stored.State.ShouldBe(ToolCallState.Running);
            stored.AttemptCount.ShouldBe(1, customMessage: "the head is the database's to advance, never a writer's");
            stored.NextAttemptOrdinal.ShouldBe(2);
            stored.Revision.ShouldBe(2);
        }

        // A retry is the next physical try, not a rewrite of the first one — which is the whole reason the plane splits.
        var first = await ReadAttemptAsync(call, ordinal: 1);
        await FailAsync(first, "tool.transport-reset");
        var second = await SeedAttemptAsync(world, call, ordinal: 2, retryOf: first);
        await SucceedAsync(second);

        var terminalAt = DateTimeOffset.UtcNow;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == call.Id);
            stored.State = ToolCallState.Completed;
            stored.TerminalAt = terminalAt;
            stored.LastModifiedAt = terminalAt;
            stored.Revision++;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == call.Id);
            stored.State.ShouldBe(ToolCallState.Completed);
            stored.AttemptCount.ShouldBe(2);

            var attempts = await db.WorkflowRunToolCallAttempt.Where(candidate => candidate.ToolCallId == call.Id)
                .OrderBy(candidate => candidate.AttemptOrdinal).ToListAsync();
            attempts.Select(attempt => attempt.AttemptOrdinal).ShouldBe(new[] { 1, 2 });
            attempts[0].Status.ShouldBe(ToolCallAttemptStatus.Failed);
            attempts[0].CompletedAt.ShouldNotBeNull(customMessage: "per-attempt timing is the fact the audit found nowhere");
            attempts[1].Status.ShouldBe(ToolCallAttemptStatus.Succeeded);
            attempts[1].RetryOfAttemptId.ShouldBe(attempts[0].Id, customMessage: "\"did it retry\" must be answerable from the row, not reconstructed");
            attempts[1].ResultDigest.ShouldNotBeNull(customMessage: "the audit's finding was that only an INPUT hash was ever kept");
        }
    }

    [Fact]
    public async Task Attempt_ordinals_are_contiguous_from_one_and_the_head_is_not_writable_by_hand()
    {
        var world = await SeedRunAsync();
        var call = await SeedCallAsync(world);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunToolCallAttempt.Add(Attempt(world, call, ordinal: 2));
            var gap = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            gap.InnerException?.Message.ShouldContain("ordinals are contiguous from one");
        }

        // A head a writer can move by hand is a head that can name an attempt nobody appended.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == call.Id);
            stored.State = ToolCallState.Running;
            stored.AttemptCount = 1;
            stored.NextAttemptOrdinal = 2;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var phantom = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            phantom.InnerException?.Message.ShouldContain("head advance requires its exact appended attempt");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == call.Id);
            stored.State = ToolCallState.Running;
            stored.AttemptCount = 2;
            stored.NextAttemptOrdinal = 3;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var doubled = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            doubled.InnerException?.Message.ShouldContain("attempt-head advances are exactly one appended attempt");
        }

        await SeedAttemptAsync(world, call, ordinal: 1);
        await SucceedAsync(await ReadAttemptAsync(call, ordinal: 1));

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunToolCallAttempt.Add(Attempt(world, call, ordinal: 3));
            var skipped = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            skipped.InnerException?.Message.ShouldContain("ordinals are contiguous from one");
        }

        using (var scope = _fixture.BeginScope())
        {
            (await Calls(scope).SingleAsync(candidate => candidate.Id == call.Id)).AttemptCount
                .ShouldBe(1, customMessage: "a refused attempt must not have moved the head");
        }
    }

    /// <summary>
    /// Two tries live at once means a side-effecting tool is running twice with one audit row to show for it. The gate
    /// is the terminal of the previous try, and nothing else.
    /// </summary>
    [Fact]
    public async Task Exactly_one_attempt_may_be_in_flight_and_the_next_one_waits_for_its_terminal()
    {
        var world = await SeedRunAsync();
        var call = await SeedCallAsync(world);
        await SeedAttemptAsync(world, call, ordinal: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunToolCallAttempt.Add(Attempt(world, call, ordinal: 2));
            var concurrent = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            concurrent.InnerException?.Message.ShouldContain("allows exactly one attempt in flight per tool call");
        }

        var first = await ReadAttemptAsync(call, ordinal: 1);
        await FailAsync(first, "tool.timeout");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunToolCallAttempt.Add(Attempt(world, call, ordinal: 2, retryOf: first));
            await db.SaveChangesAsync();
        }

        // Reopening a finished try is the other way to end up with two live attempts.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCallAttempt.SingleAsync(candidate => candidate.Id == first.Id);
            stored.Status = ToolCallAttemptStatus.Running;
            stored.CompletedAt = null;
            stored.ErrorCode = null;
            stored.ErrorMessage = null;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var revived = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            revived.InnerException?.Message.ShouldContain("terminal status is immutable");
        }
    }

    /// <summary>
    /// "A terminal call's attempts are all terminal", pinned from all three directions a writer could break it:
    /// closing over a live try, appending to a closed call, and reviving a try after the call closed.
    /// </summary>
    [Fact]
    public async Task A_terminal_call_has_no_live_attempt_in_either_direction()
    {
        var world = await SeedRunAsync();
        var call = await SeedCallAsync(world);
        await SeedAttemptAsync(world, call, ordinal: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var terminalAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == call.Id);
            stored.State = ToolCallState.Completed;
            stored.TerminalAt = terminalAt;
            stored.LastModifiedAt = terminalAt;
            stored.Revision++;
            var overLive = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            overLive.InnerException?.Message.ShouldContain("cannot close while an attempt is still in flight");
        }

        var attempt = await ReadAttemptAsync(call, ordinal: 1);
        await SucceedAsync(attempt);
        await CompleteAsync(call);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunToolCallAttempt.Add(Attempt(world, call, ordinal: 2));
            var afterTerminal = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            afterTerminal.InnerException?.Message.ShouldContain("requires its live tenant-bound tool call");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == call.Id);
            stored.State = ToolCallState.Running;
            stored.TerminalAt = null;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var reopened = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            reopened.InnerException?.Message.ShouldContain("terminal state is immutable");
        }
    }

    /// <summary>
    /// Indeterminate is the one status meaning "this side effect may or may not have landed", so a call carrying one
    /// may not roll it up into a clean Completed — that is the same collapse the attempt status forbids one level
    /// down. It closes Abandoned, which owes an error_code. A KNOWN failure is a different fact: the retried-then-
    /// succeeded call in <see cref="Appending_attempts_advances_the_call_head_and_a_finished_call_completes"/> still
    /// closes Completed over its Failed first try.
    /// </summary>
    [Fact]
    public async Task A_call_whose_effect_may_have_landed_cannot_close_as_a_clean_completed()
    {
        var world = await SeedRunAsync();
        var call = await SeedCallAsync(world);
        await SeedAttemptAsync(world, call, ordinal: 1);
        await IndeterminateAsync(await ReadAttemptAsync(call, ordinal: 1));

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var terminalAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == call.Id);
            stored.State = ToolCallState.Completed;
            stored.TerminalAt = terminalAt;
            stored.LastModifiedAt = terminalAt;
            stored.Revision++;
            var clean = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            clean.InnerException?.Message.ShouldContain("cannot close as Completed over an attempt whose effect is unknown",
                customMessage: "a call whose only try may or may not have landed must not read as a clean Completed with no error_code");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var terminalAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == call.Id);
            stored.State = ToolCallState.Abandoned;
            stored.TerminalAt = terminalAt;
            stored.LastModifiedAt = terminalAt;
            stored.ErrorCode = "tool.indeterminate";
            stored.Revision++;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var stored = await Calls(scope).SingleAsync(candidate => candidate.Id == call.Id);
            stored.State.ShouldBe(ToolCallState.Abandoned);
            stored.ErrorCode.ShouldNotBeNull(customMessage: "the only close left to an unknown outcome is the one that owes a typed reason");
        }
    }

    /// <summary>
    /// The redaction discipline as teeth rather than convention. A tool argument can carry a credential, so referenced
    /// bytes must NAME the pass that cleared them — which is what turns a writer that skipped redaction into a failed
    /// INSERT — and content that was never referenced can never be claimed as exact content.
    /// </summary>
    [Fact]
    public async Task Referenced_content_must_name_a_redaction_pass_and_absent_content_cannot_claim_exactness()
    {
        var world = await SeedRunAsync();
        const string redaction = "ck_workflow_run_tool_call_redaction";

        await RejectsCallAsync(world, redaction, call => call.RedactionPolicy = null);
        await RejectsCallAsync(world, redaction, call => call.ArgumentsRedaction = null);
        await RejectsCallAsync(world, redaction, call => call.ArgumentsDigest = null);
        await RejectsCallAsync(world, redaction, call => call.ArgumentsDigest = "not-a-sha-256");
        await RejectsCallAsync(world, redaction, call => call.CaptureCompleteness = WorkflowRunCaptureCompleteness.Exact);
        await RejectsCallAsync(world, redaction, call => call.ArgumentsRedaction = NativeRecordRedaction.Withheld);

        // "Nothing captured yet" may not masquerade as exact capture — that is how missing content reads as empty.
        await RejectsCallAsync(world, redaction, call =>
        {
            call.ArgumentsArtifactId = null;
            call.ArgumentsDigest = null;
            call.ArgumentsRedaction = null;
            call.RedactionPolicy = null;
            call.CaptureCompleteness = WorkflowRunCaptureCompleteness.RedactedExact;
        });

        // ...and a deliberate Withheld must reference nothing and claim exactly Unavailable.
        await RejectsCallAsync(world, redaction, call =>
        {
            call.ArgumentsArtifactId = null;
            call.ArgumentsDigest = null;
            call.ArgumentsRedaction = NativeRecordRedaction.Withheld;
            call.RedactionPolicy = null;
            call.CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial;
        });

        var call = await SeedCallAsync(world);
        var attempt = await SeedAttemptAsync(world, call, ordinal: 1);
        const string attemptRedaction = "ck_workflow_run_tool_call_attempt_redaction";

        await RejectsResultAsync(attempt, attemptRedaction, stored => stored.RedactionPolicy = null);
        await RejectsResultAsync(attempt, attemptRedaction, stored => stored.ResultRedaction = null);
        await RejectsResultAsync(attempt, attemptRedaction, stored => stored.ResultDigest = null);
        await RejectsResultAsync(attempt, attemptRedaction, stored => stored.CaptureCompleteness = WorkflowRunCaptureCompleteness.Exact);

        // The ERROR body is the other referenced payload, and it is not exempt: a tool's error can quote a credential
        // back as readily as its result, and a reference nobody can verify is a reference no audit can trust.
        await RejectsResultAsync(attempt, attemptRedaction, stored => ErrorOnly(stored, digest: null));
        await RejectsResultAsync(attempt, attemptRedaction, stored => ErrorOnly(stored, digest: "not-a-sha-256"));

        // ...and the pair holds in the other direction too: a digest with nothing to digest names bytes nobody kept.
        await RejectsResultAsync(attempt, attemptRedaction, stored => stored.ErrorDigest = Digest('f'));

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCallAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            ErrorOnly(stored, Digest('f'));
            stored.Status = ToolCallAttemptStatus.Failed;
            stored.ErrorCode = "tool.transport-reset";
            stored.CompletedAt = DateTimeOffset.UtcNow;
            stored.LastModifiedAt = stored.CompletedAt.Value;
            stored.Revision++;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>States an ERROR-only capture — the tool answered with an error body and nothing else — so the error payload's own digest rule is offered the same table of forgeries the result's is.</summary>
    private static void ErrorOnly(WorkflowRunToolCallAttempt attempt, string? digest)
    {
        attempt.ResultArtifactId = null;
        attempt.ResultDigest = null;
        attempt.ErrorArtifactId = Guid.NewGuid();
        attempt.ErrorDigest = digest;
        attempt.ResultRedaction = NativeRecordRedaction.Masked;
        attempt.RedactionPolicy = RedactionPolicy;
        attempt.CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial;
    }

    /// <summary>
    /// A capture statement is made once. Upgrading a Withheld decision into bytes would retroactively contradict a
    /// deliberate choice not to capture them, and replacing a stated reference would rewrite what a reader already
    /// audited while the digest it verified still named the old bytes.
    /// </summary>
    [Fact]
    public async Task Stated_capture_is_immutable_so_a_withheld_decision_is_never_upgraded_into_bytes()
    {
        var world = await SeedRunAsync();
        var withheld = await SeedCallAsync(world, call =>
        {
            call.ArgumentsArtifactId = null;
            call.ArgumentsDigest = null;
            call.ArgumentsRedaction = NativeRecordRedaction.Withheld;
            call.RedactionPolicy = null;
            call.CaptureCompleteness = WorkflowRunCaptureCompleteness.Unavailable;
        });

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == withheld.Id);
            stored.ArgumentsArtifactId = Guid.NewGuid();
            stored.ArgumentsDigest = Digest('b');
            stored.ArgumentsRedaction = NativeRecordRedaction.Masked;
            stored.RedactionPolicy = RedactionPolicy;
            stored.CaptureCompleteness = WorkflowRunCaptureCompleteness.RedactedExact;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var upgraded = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            upgraded.InnerException?.Message.ShouldContain("stated arguments capture is immutable");
        }

        var stated = await SeedCallAsync(world, call => call.CallOrdinal = 2);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == stated.Id);
            stored.ArgumentsArtifactId = Guid.NewGuid();
            stored.ArgumentsDigest = Digest('c');
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var swapped = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            swapped.InnerException?.Message.ShouldContain("stated arguments capture is immutable");
        }

        // The completeness itself is NOT one of the pinned columns, so an evidence downgrade found while the call is
        // still live can be recorded...
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == stated.Id);
            stored.CaptureCompleteness = WorkflowRunCaptureCompleteness.Corrupt;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            await db.SaveChangesAsync();
        }

        // ...and the terminal row is frozen ENTIRELY, this column included. Pinned because the honest scope of that
        // mutability is exactly what a doc-comment can overstate into "a plane can always later record Corrupt".
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var terminalAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == stated.Id);
            stored.State = ToolCallState.Abandoned;
            stored.TerminalAt = terminalAt;
            stored.LastModifiedAt = terminalAt;
            stored.ErrorCode = "tool.never-dispatched";
            stored.Revision++;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == stated.Id);
            stored.CaptureCompleteness = WorkflowRunCaptureCompleteness.LegacyUnknown;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var afterClose = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            afterClose.InnerException?.Message.ShouldContain("terminal state is immutable",
                customMessage: "completeness is mutable only while the call is live, so no doc may promise a later Corrupt on a closed row");
        }
    }

    [Fact]
    public async Task Retry_lineage_must_point_at_an_earlier_finished_attempt_of_the_same_call()
    {
        var world = await SeedRunAsync();
        var call = await SeedCallAsync(world);
        var other = await SeedCallAsync(world, candidate => candidate.CallOrdinal = 2);
        var borrowed = await SeedAttemptAsync(world, other, ordinal: 1);
        await SucceedAsync(borrowed);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var forged = Attempt(world, call, ordinal: 1);
            forged.RetryOfAttemptId = borrowed.Id;
            forged.RetryReason = "tool.transport-reset";
            db.WorkflowRunToolCallAttempt.Add(forged);
            var firstTry = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();

            // Refused by the GUARD, not by ck_..._attempt_retry's attempt_ordinal > 1 arm: a BEFORE ROW trigger runs
            // ahead of constraint checking, and at ordinal one there is no earlier same-call attempt for retry_of to
            // name, so the lookup misses first. That arm's own spelling is pinned in the model + drift detector.
            firstTry.InnerException?.Message.ShouldContain("may only retry an earlier finished attempt of the same call",
                customMessage: "the first try retries nothing, so a self-declared retry at ordinal one is a forged lineage");
        }

        await SeedAttemptAsync(world, call, ordinal: 1);
        await FailAsync(await ReadAttemptAsync(call, ordinal: 1), "tool.transport-reset");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var crossCall = Attempt(world, call, ordinal: 2);
            crossCall.RetryOfAttemptId = borrowed.Id;
            crossCall.RetryReason = "tool.transport-reset";
            db.WorkflowRunToolCallAttempt.Add(crossCall);
            var foreign = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            foreign.InnerException?.Message.ShouldContain("may only retry an earlier finished attempt of the same call");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var reasonless = Attempt(world, call, ordinal: 2);
            reasonless.RetryOfAttemptId = (await ReadAttemptAsync(call, ordinal: 1)).Id;
            db.WorkflowRunToolCallAttempt.Add(reasonless);
            var silent = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            silent.InnerException?.Message.ShouldContain("ck_workflow_run_tool_call_attempt_retry",
                customMessage: "a retry that cannot say why it happened is the fact the audit needed most");
        }
    }

    [Fact]
    public async Task Invocation_identity_and_source_admission_are_immutable()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var call = await SeedCallAsync(world, candidate =>
        {
            candidate.SourceKind = "tool-call-ledger/v1";
            candidate.SourceCorrelationId = correlationId;
        });

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == call.Id);
            stored.ToolName = "repo_read";
            stored.EffectClass = ToolCallEffectClass.ReadOnly;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var rebranded = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            rebranded.InnerException?.Message.ShouldContain("stable invocation identity is immutable",
                customMessage: "a side-effecting call must not be able to relabel itself read-only after the fact");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == call.Id);
            stored.SourceCorrelationId = Guid.NewGuid();
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var restated = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            restated.InnerException?.Message.ShouldContain("source identity is immutable");
        }

        // The projection's admission key. It deduplicates the ROW; tool_call_ledger keeps exactly-once for the effect.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var duplicate = Call(world);
            duplicate.CallOrdinal = 3;
            duplicate.SourceKind = "tool-call-ledger/v1";
            duplicate.SourceCorrelationId = correlationId;
            db.WorkflowRunToolCall.Add(duplicate);
            var replayed = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            replayed.InnerException?.Message.ShouldContain("ux_workflow_run_tool_call_source_identity");
        }
    }

    [Fact]
    public async Task Column_checks_reject_an_unversioned_tool_kind_a_reasonless_terminal_and_an_empty_clean_close()
    {
        var world = await SeedRunAsync();

        await RejectsCallAsync(world, "ck_workflow_run_tool_call_identity", call => call.ToolKind = "repo_write");
        await RejectsCallAsync(world, "ck_workflow_run_tool_call_identity", call => call.ToolName = "   ");
        await RejectsCallAsync(world, "ck_workflow_run_tool_call_head", call => call.CallOrdinal = 0);
        await RejectsCallAsync(world, "ck_workflow_run_tool_call_execution_identity", call => call.ExecutionAttemptId = Guid.NewGuid());

        var empty = await SeedCallAsync(world);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var terminalAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == empty.Id);
            stored.State = ToolCallState.Completed;
            stored.TerminalAt = terminalAt;
            stored.LastModifiedAt = terminalAt;
            stored.Revision++;
            var cleanExit = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            cleanExit.InnerException?.Message.ShouldContain("ck_workflow_run_tool_call_terminal",
                customMessage: "an invocation that never ran an attempt must not be closable as a clean Completed");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var terminalAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == empty.Id);
            stored.State = ToolCallState.Abandoned;
            stored.TerminalAt = terminalAt;
            stored.LastModifiedAt = terminalAt;
            stored.Revision++;
            var reasonless = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            reasonless.InnerException?.Message.ShouldContain("ck_workflow_run_tool_call_terminal",
                customMessage: "an abandoned invocation owes a reason, or an unknown outcome reads as a clean one");
        }

        var attempt = await SeedAttemptAsync(world, empty, ordinal: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var completedAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunToolCallAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.Status = ToolCallAttemptStatus.Indeterminate;
            stored.CompletedAt = completedAt;
            stored.LastModifiedAt = completedAt;
            stored.Revision++;
            var silent = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            silent.InnerException?.Message.ShouldContain("ck_workflow_run_tool_call_attempt_terminal",
                customMessage: "a try whose effect may or may not have landed must carry a typed reason");
        }
    }

    /// <summary>
    /// The attempt's denormalized team and run must belong to its own call, or a cross-tenant row hides behind matching
    /// column names. The composite parent key is what proves it rather than trusting a writer to stamp both twice.
    /// </summary>
    [Fact]
    public async Task An_attempt_cannot_borrow_another_scope_than_its_own_calls()
    {
        var world = await SeedRunAsync();
        var call = await SeedCallAsync(world);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var borrowed = Attempt(world, call, ordinal: 1);
        borrowed.WorkflowRunId = Guid.NewGuid();
        db.WorkflowRunToolCallAttempt.Add(borrowed);

        var rejected = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();

        rejected.InnerException?.Message.ShouldContain("requires its live tenant-bound tool call");
    }

    /// <summary>
    /// Retention discipline, and a deliberate difference from the harness execution plane: a plane with many rows per
    /// run must stay PRUNABLE, so DELETE is permitted here and cascades. If it were rejected, the only way to reclaim
    /// a busy run's tool history would be to drop the run row it is anchored to.
    ///
    /// <para>Prunable at the CALL, though. attempt_count and next_attempt_ordinal are a DERIVED head that no DELETE
    /// walks back, so a piecemeal attempt delete leaves the call naming a try that no longer exists — and closing it
    /// as Completed still passes the attempt_count &gt; 0 arm on the strength of that phantom.</para>
    /// </summary>
    [Fact]
    public async Task Pruning_a_call_cascades_to_its_attempts()
    {
        var world = await SeedRunAsync();
        var call = await SeedCallAsync(world);
        var attempt = await SeedAttemptAsync(world, call, ordinal: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var piecemeal = await Should.ThrowAsync<PostgresException>(
                db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM workflow_run_tool_call_attempt WHERE id = {attempt.Id}"));
            piecemeal.Message.ShouldContain("cannot be deleted while its call still exists",
                customMessage: "a deleted try leaves the head naming an attempt nobody can read, and the call closes claiming it");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM workflow_run_tool_call WHERE id = {call.Id}");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRunToolCall.CountAsync(candidate => candidate.Id == call.Id)).ShouldBe(0);
            (await db.WorkflowRunToolCallAttempt.CountAsync(candidate => candidate.ToolCallId == call.Id)).ShouldBe(0,
                customMessage: "an orphaned attempt row outlives the invocation it describes and can never be interpreted again");
        }
    }

    /// <summary>
    /// The unique indexes are the CONCURRENCY backstop. In one session the guard rejects the illegal row first, but two
    /// writers racing past their own snapshots see no conflict and only the index does — so the counter-example here is
    /// the index's own existence, uniqueness and columns. Asserting that a duplicate insert throws would prove the
    /// trigger, not the index that exists to back it.
    /// </summary>
    [Theory]
    [InlineData("ux_workflow_run_tool_call_attempt_ordinal", "workflow_run_tool_call_attempt", "(team_id, tool_call_id, attempt_ordinal)", "")]
    [InlineData("ux_workflow_run_tool_call_attempt_in_flight", "workflow_run_tool_call_attempt", "(team_id, tool_call_id)", "WHERE")]
    [InlineData("ux_workflow_run_tool_call_source_identity", "workflow_run_tool_call", "(team_id, workflow_run_id, source_kind, source_correlation_id)", "WHERE")]
    public async Task Concurrency_backstop_index_is_installed_and_unique(string indexName, string tableName, string expectedColumns, string expectedFilter)
    {
        var definitions = await IndexDefinitionsAsync(tableName, indexName);

        definitions.ShouldHaveSingleItem(
            customMessage: $"index '{indexName}' must exist after 0141 applies — without it two racing writers each pass the trigger against their own snapshot. Diagnose with: psql -c '\\di {indexName}'.");
        definitions[0].ShouldStartWith("CREATE UNIQUE",
            customMessage: $"index '{indexName}' exists but is not UNIQUE, so it rejects nothing.");
        definitions[0].ShouldContain(expectedColumns,
            customMessage: $"index '{indexName}' is unique over the wrong columns, so the race it exists to lose stays winnable. Diagnose with: psql -c '\\d {tableName}'.");
        if (expectedFilter.Length > 0)
            definitions[0].ShouldContain(expectedFilter, customMessage: $"index '{indexName}' must stay partial, or it forbids rows it was never meant to see.");
    }

    private async Task<IReadOnlyList<string>> IndexDefinitionsAsync(string tableName, string indexName)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = @table AND indexname = @index", connection);
        command.Parameters.AddWithValue("table", tableName);
        command.Parameters.AddWithValue("index", indexName);
        await using var reader = await command.ExecuteReaderAsync();
        var definitions = new List<string>();
        while (await reader.ReadAsync()) definitions.Add(reader.GetString(0));
        return definitions;
    }

    private static IQueryable<WorkflowRunToolCall> Calls(ILifetimeScope scope) => scope.Resolve<CodeSpaceDbContext>().WorkflowRunToolCall.AsNoTracking();

    private async Task RejectsCallAsync(RunWorld world, string constraintName, Action<WorkflowRunToolCall> forge)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var call = Call(world);
        forge(call);
        db.WorkflowRunToolCall.Add(call);

        var rejected = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();

        rejected.InnerException?.Message.ShouldContain(constraintName);
    }

    /// <summary>Offers one otherwise-legal captured RESULT with a single field forged, so a whole table of illegal capture shapes reads as one line each instead of one scope each.</summary>
    private async Task RejectsResultAsync(WorkflowRunToolCallAttempt attempt, string constraintName, Action<WorkflowRunToolCallAttempt> forge)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var completedAt = DateTimeOffset.UtcNow;
        var stored = await db.WorkflowRunToolCallAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
        stored.ResultArtifactId = Guid.NewGuid();
        stored.ResultDigest = Digest('d');
        stored.ResultRedaction = NativeRecordRedaction.Masked;
        stored.RedactionPolicy = RedactionPolicy;
        stored.CaptureCompleteness = WorkflowRunCaptureCompleteness.RedactedExact;
        stored.Status = ToolCallAttemptStatus.Succeeded;
        stored.CompletedAt = completedAt;
        stored.LastModifiedAt = completedAt;
        stored.Revision++;
        forge(stored);

        var rejected = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();

        rejected.InnerException?.Message.ShouldContain(constraintName);
    }

    private async Task<WorkflowRunToolCallAttempt> ReadAttemptAsync(WorkflowRunToolCall call, int ordinal)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunToolCallAttempt.AsNoTracking()
            .SingleAsync(candidate => candidate.ToolCallId == call.Id && candidate.AttemptOrdinal == ordinal);
    }

    private async Task SucceedAsync(WorkflowRunToolCallAttempt attempt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var completedAt = DateTimeOffset.UtcNow;
        var stored = await db.WorkflowRunToolCallAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
        stored.Status = ToolCallAttemptStatus.Succeeded;
        stored.ResultArtifactId = Guid.NewGuid();
        stored.ResultDigest = Digest('e');
        stored.ResultRedaction = NativeRecordRedaction.Masked;
        stored.RedactionPolicy = RedactionPolicy;
        stored.CaptureCompleteness = WorkflowRunCaptureCompleteness.RedactedExact;
        stored.CompletedAt = completedAt;
        stored.LastModifiedAt = completedAt;
        stored.Revision++;
        await db.SaveChangesAsync();
    }

    private async Task IndeterminateAsync(WorkflowRunToolCallAttempt attempt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var completedAt = DateTimeOffset.UtcNow;
        var stored = await db.WorkflowRunToolCallAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
        stored.Status = ToolCallAttemptStatus.Indeterminate;
        stored.ErrorCode = "tool.indeterminate";
        stored.ErrorMessage = "the fabric dropped after dispatch, so the write may or may not have landed";
        stored.CompletedAt = completedAt;
        stored.LastModifiedAt = completedAt;
        stored.Revision++;
        await db.SaveChangesAsync();
    }

    private async Task FailAsync(WorkflowRunToolCallAttempt attempt, string errorCode)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var completedAt = DateTimeOffset.UtcNow;
        var stored = await db.WorkflowRunToolCallAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
        stored.Status = ToolCallAttemptStatus.Failed;
        stored.ErrorCode = errorCode;
        stored.ErrorMessage = "the fabric closed the connection before the tool answered";
        stored.CompletedAt = completedAt;
        stored.LastModifiedAt = completedAt;
        stored.Revision++;
        await db.SaveChangesAsync();
    }

    private async Task CompleteAsync(WorkflowRunToolCall call)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var terminalAt = DateTimeOffset.UtcNow;
        var stored = await db.WorkflowRunToolCall.SingleAsync(candidate => candidate.Id == call.Id);
        stored.State = ToolCallState.Completed;
        stored.TerminalAt = terminalAt;
        stored.LastModifiedAt = terminalAt;
        stored.Revision++;
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowRunToolCall> SeedCallAsync(RunWorld world, Action<WorkflowRunToolCall>? configure = null)
    {
        var call = Call(world);
        configure?.Invoke(call);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunToolCall.Add(call);
        await db.SaveChangesAsync();
        return call;
    }

    private async Task<WorkflowRunToolCallAttempt> SeedAttemptAsync(RunWorld world, WorkflowRunToolCall call, int ordinal, WorkflowRunToolCallAttempt? retryOf = null)
    {
        var attempt = Attempt(world, call, ordinal, retryOf);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunToolCallAttempt.Add(attempt);
        await db.SaveChangesAsync();
        return attempt;
    }

    private async Task<RunWorld> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "tool-call-plane-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return new RunWorld(await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId), teamId);
    }

    /// <summary>A canonical lowercase SHA-256 shape; the value is irrelevant, its FORM is what the schema pins.</summary>
    private static string Digest(char fill) => new(fill, 64);

    private static WorkflowRunToolCall Call(RunWorld world)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowRunToolCall
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, WorkflowRunId = world.RunId, NodeId = "agent",
            IterationKey = "agent#turn1", CallOrdinal = 1, Purpose = "agent.edit/v1",
            ToolKind = "mcp.codespace.repo-write/v1", ToolNamespace = "codespace-mcp", ToolName = "repo_write",
            EffectClass = ToolCallEffectClass.SideEffecting, ArgumentsArtifactId = Guid.NewGuid(),
            ArgumentsDigest = Digest('a'), ArgumentsRedaction = NativeRecordRedaction.Masked,
            RedactionPolicy = RedactionPolicy, CaptureSource = "harness-native",
            CaptureCompleteness = WorkflowRunCaptureCompleteness.RedactedExact, State = ToolCallState.Pending,
            AttemptCount = 0, NextAttemptOrdinal = 1, Revision = 1,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now, LastModifiedAt = now,
        };
    }

    private static WorkflowRunToolCallAttempt Attempt(RunWorld world, WorkflowRunToolCall call, int ordinal, WorkflowRunToolCallAttempt? retryOf = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowRunToolCallAttempt
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, WorkflowRunId = world.RunId, ToolCallId = call.Id,
            AttemptOrdinal = ordinal, RetryOfAttemptId = retryOf?.Id,
            RetryReason = retryOf is null ? null : "tool.transport-reset", TransportKind = "mcp-uds/v1",
            EndpointFingerprint = "uds:/run/tool.sock", InvocationId = $"jsonrpc-{call.Id:N}-{ordinal}",
            Status = ToolCallAttemptStatus.Running, CaptureSource = "harness-native",
            CaptureCompleteness = WorkflowRunCaptureCompleteness.Unavailable, StartedAt = now, Revision = 1,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now, LastModifiedAt = now,
        };
    }

    private sealed record RunWorld(Guid RunId, Guid TeamId);
}
