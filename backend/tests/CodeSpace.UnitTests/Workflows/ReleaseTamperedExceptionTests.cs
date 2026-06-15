using CodeSpace.Core.Services.Workflows.Engine;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the two flavours of <see cref="ReleaseTamperedException"/> — the authored-version variant
/// (carries the workflow id + version that drifted) and the snapshot-run variant (carries the run
/// id, since a snapshot run has no workflow/version pin). Both must name "tampering" in the message
/// so the engine's bootstrap-failure ledger surfaces a diagnosable cause to the operator (the
/// SnapshotRunFlowTests tamper assertion greps for it).
/// </summary>
[Trait("Category", "Unit")]
public class ReleaseTamperedExceptionTests
{
    [Fact]
    public void Authored_variant_names_the_workflow_version_and_both_hashes()
    {
        var workflowId = Guid.NewGuid();

        var ex = new ReleaseTamperedException(workflowId, 7, storedHash: "aaa", recomputedHash: "bbb");

        ex.WorkflowId.ShouldBe(workflowId);
        ex.WorkflowVersion.ShouldBe(7);
        ex.StoredHash.ShouldBe("aaa");
        ex.RecomputedHash.ShouldBe("bbb");
        ex.Message.ShouldContain("tampering");
        ex.Message.ShouldContain("aaa");
        ex.Message.ShouldContain("bbb");
    }

    [Fact]
    public void Snapshot_variant_names_the_run_id_and_both_hashes()
    {
        var runId = Guid.NewGuid();

        var ex = ReleaseTamperedException.ForSnapshot(runId, storedHash: "stored123", recomputedHash: "recomputed456");

        // The engine's bootstrap-failure ledger greps for "tampering"; a snapshot run is identified
        // by its run id (not a workflow/version pin); the column name distinguishes this from the
        // authored-version message.
        ex.Message.ShouldContain("tampering");
        ex.Message.ShouldContain(runId.ToString());
        ex.Message.ShouldContain("stored123");
        ex.Message.ShouldContain("recomputed456");
        ex.Message.ShouldContain("definition_snapshot_jsonb");
    }
}
