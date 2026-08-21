import { useMemo } from "react";

import type { WorkflowRunCellFieldRangeAvailability, WorkflowRunCellFieldReadIdentity } from "@/api/workflowRunCellFieldRangeApi";
import { useWorkflowRunCellFieldContent } from "@/hooks/use-workflow-run-cell-field-content";

import { JsonView } from "./JsonView";

function failureLabel(availability: WorkflowRunCellFieldRangeAvailability): string {
  switch (availability) {
    case "NotRecorded": return "Field content was not recorded.";
    case "StaleIdentity": return "This field descriptor is stale; reopen the cell.";
    case "CorruptReference": return "The field's stored reference is corrupt.";
    case "MetadataMissing": return "Field artifact metadata is missing.";
    case "PhysicalObjectMissing": return "Stored field bytes are missing.";
    case "IntegrityFailure": return "Field content failed its integrity checks.";
    case "BackendUnavailable": return "The field storage backend is unavailable.";
    case "AccessDenied": return "Access to the stored field bytes was denied.";
    case "InvalidRange": return "The field byte range is invalid.";
    default: return "Field content is unavailable.";
  }
}

/** Presentational lazy body for a descriptor UI. The caller must set expanded only after a user opens that descriptor. */
export function WorkflowRunCellFieldContent({ identity, expanded }: { identity: WorkflowRunCellFieldReadIdentity; expanded: boolean }) {
  const content = useWorkflowRunCellFieldContent(identity, expanded);
  const visibleText = content.pages.map((page) => page.text ?? "").join("");
  const completeText = content.pages.length === 1 && !content.earlierOmitted && content.pages[0].offsetBytes === 0
    && content.pages[0].completeJsonValue ? content.pages[0].text : null;
  const parsed = useMemo(() => {
    if (completeText === null) return null;
    try { return { value: JSON.parse(completeText) as unknown }; } catch { return null; }
  }, [completeText]);
  const hasUnverifiedArtifactWindow = content.pages.some((page) => page.source === "Artifact" && !page.integrityVerified);

  if (!expanded) return null;
  if (content.loading) return <div className="wf-cell-field-empty">Loading selected field content…</div>;
  if (content.missing) return <div className="wf-cell-field-empty">This field content is no longer available.</div>;
  if (content.error) return <div className="wf-cell-field-error" role="status">Could not safely read this field content.</div>;
  if (content.failure && content.pages.length === 0) {
    return (
      <div className="wf-cell-field-error" role="status">
        <span>{failureLabel(content.failure.availability)}</span>
        {content.canRetry && <button type="button" onClick={content.retry}>Retry field content</button>}
      </div>
    );
  }
  if (content.pages.length === 0) return <div className="wf-cell-field-empty">No field bytes are available.</div>;

  return (
    <section className="wf-cell-field-content" aria-label="Selected Workflow Run cell field content">
      {content.failure && (
        <div className="wf-cell-field-error" role="status">
          <span>{failureLabel(content.failure.availability)}</span>
          {content.canRetry && <button type="button" onClick={content.retry}>Retry field content</button>}
        </div>
      )}
      {parsed === null ? <pre>{visibleText}</pre> : <JsonView data={parsed.value} />}
      <div className="wf-cell-field-paging">
        <span>Page {content.pagesRead} of at least {content.pagesRead}</span>
        {hasUnverifiedArtifactWindow && <span>This artifact byte window is not end-to-end integrity verified.</span>}
        {content.earlierOmitted && <span>Earlier field bytes were omitted from this 512 KiB local window.</span>}
        {!content.failure && content.hasNextPage && (
          <button type="button" disabled={content.loadingMore} onClick={() => void content.loadNext()}>
            {content.loadingMore ? "Loading…" : "Load next page"}
          </button>
        )}
        {content.earlierOmitted && <button type="button" onClick={content.returnToStart}>Return to start</button>}
      </div>
    </section>
  );
}
