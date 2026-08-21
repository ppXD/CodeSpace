import { useState } from "react";

import type { WorkflowRunCellFieldAvailability, WorkflowRunCellFieldDescriptor } from "@/api/workflowRunCellFieldsApi";
import type { WorkflowRunCellFieldReadIdentity } from "@/api/workflowRunCellFieldRangeApi";
import type { WorkflowRunLazyFieldRead } from "@/api/workflowRunViewMetadataApi";
import { useWorkflowRunCellFields } from "@/hooks/use-workflow-run-cell-fields";

import { WorkflowRunCellFieldContent } from "./WorkflowRunCellFieldContent";

function availabilityLabel(value: WorkflowRunCellFieldAvailability): string {
  switch (value) {
    case "Available": return "Available";
    case "NotRecorded": return "Not recorded";
    case "CorruptReference": return "Corrupt stored reference";
    case "NameTooLarge": return "A field name exceeded the safe display bound";
    case "Truncated": return "More fields remain";
    case "Unavailable": return "Unavailable or malformed";
  }
}

function fieldKey(field: WorkflowRunCellFieldDescriptor): string { return `${field.section}\n${field.name ?? ""}`; }

export function WorkflowRunCellFields({ read }: { read: WorkflowRunLazyFieldRead }) {
  const fields = useWorkflowRunCellFields(read, true);
  const [selected, setSelected] = useState<string | null>(null);

  if (fields.loading) return <div className="wf-rf-result-empty">Loading recorded fields…</div>;
  if (fields.missing) return <div className="wf-rf-result-empty">This cell observation is no longer current.</div>;
  if (fields.error && fields.fields.length === 0) return (
    <div className="wf-rf-result-err" role="status">
      Could not safely read recorded field metadata.
      {fields.retryable && <button type="button" onClick={fields.retry}>Retry field metadata</button>}
    </div>
  );
  if (fields.identity === null) return <div className="wf-rf-result-empty">No exact cell observation is available.</div>;

  const page = fields.identity;
  return (
    <div className="wf-cell-fields" data-pages={fields.pagesRead}>
      {fields.error && (
        <div className="wf-rf-result-err" role="status">
          More field metadata could not be loaded; the prior bounded window remains visible.
          {fields.retryable && <button type="button" onClick={fields.retry}>Retry field metadata</button>}
        </div>
      )}
      {page.fieldsAvailability !== "Available" && page.fieldsAvailability !== "Truncated" && (
        <div className="wf-rf-result-err" role="status">{availabilityLabel(page.fieldsAvailability)}.</div>
      )}
      {fields.fields.length === 0 && page.fieldsAvailability === "Available" && (
        <div className="wf-rf-result-empty">
          Input: {availabilityLabel(page.inputsAvailability)} · Output: {availabilityLabel(page.outputsAvailability)} · Error: {availabilityLabel(page.errorAvailability)}
        </div>
      )}
      {fields.fields.map((field) => {
        const key = fieldKey(field);
        const open = selected === key;
        const identity: WorkflowRunCellFieldReadIdentity = {
          requestedRunId: read.requestedRunId,
          scope: read.scope,
          sourceRunId: read.sourceRunId,
          nodeId: read.nodeId,
          iterationKey: read.iterationKey,
          stateRecordId: page.stateRecordId,
          stateRecordSequence: page.stateRecordSequence,
          firstStartedRecordId: page.firstStartedRecordId,
          firstStartedRecordSequence: page.firstStartedRecordSequence,
          section: field.section,
          name: field.name,
        };
        return (
          <div key={key} className="wf-cell-field-descriptor" data-availability={field.availability}>
            <button type="button" disabled={field.availability !== "Available"} aria-expanded={field.availability === "Available" ? open : undefined}
              onClick={() => setSelected(open ? null : key)}>
              <span>{field.section}{field.name === null ? "" : ` · ${field.name}`}</span>
              <span>{field.jsonKind} · {field.materialization}{field.totalBytes === null ? "" : ` · ${field.totalBytes} bytes`}</span>
            </button>
            {field.availability !== "Available" && <span>{availabilityLabel(field.availability)}{field.problemCode ? ` · ${field.problemCode}` : ""}</span>}
            {open && <WorkflowRunCellFieldContent identity={identity} expanded />}
          </div>
        );
      })}
      {fields.earlierOmitted && <div className="wf-rf-result-empty">Earlier field descriptors were omitted from this 512-item local window.</div>}
      {fields.earlierOmitted && <button type="button" onClick={fields.returnToFirst}>Return to first fields</button>}
      {!fields.error && fields.hasMore && <button type="button" disabled={fields.loadingMore} onClick={() => void fields.loadMore()}>{fields.loadingMore ? "Loading…" : "Load more fields"}</button>}
    </div>
  );
}
