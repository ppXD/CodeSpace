import { describe, expect, it } from "vitest";

import type { WorkflowRunStatus } from "@/api/workflows";
import { isRunActive } from "./use-workflows";

/**
 * isRunActive drives the live-view polling: the run-detail dialog + runs list keep refreshing
 * while a run is "active" (non-terminal) and stop once it can't change anymore. The bug this
 * guards: a run that SUSPENDS (flow.sleep, a future approval wait) must stay active — otherwise
 * the live view freezes the instant a node suspends and never shows the resume / final outcome.
 */
describe("isRunActive", () => {
  it("keeps polling every non-terminal state, including Suspended and Enqueued", () => {
    const active: WorkflowRunStatus[] = ["Pending", "Enqueued", "Running", "Suspended"];
    for (const status of active) {
      expect(isRunActive(status)).toBe(true);
    }
  });

  it("stops polling once the run is terminal", () => {
    const terminal: WorkflowRunStatus[] = ["Success", "Failure", "Cancelled"];
    for (const status of terminal) {
      expect(isRunActive(status)).toBe(false);
    }
  });
});
