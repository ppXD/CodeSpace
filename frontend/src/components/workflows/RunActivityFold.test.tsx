import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { RunTimelineEvent } from "@/api/workflows";

import { RunActivityFold } from "./RunActivityFold";

const ev = (id: string, title: string): RunTimelineEvent =>
  ({ id, kind: "x", title, severity: "Info", level: "Detail", occurredAt: "2026-06-23T10:00:00Z", sourceKey: "run-record" });

function renderFold(events: RunTimelineEvent[]) {
  return render(<ol>{<RunActivityFold events={events} />}</ol>);
}

describe("RunActivityFold", () => {
  it("collapses N detail events behind a 'N steps' button, the rows hidden until expanded", () => {
    renderFold([ev("d1", "manual started"), ev("d2", "manual completed"), ev("d3", "code started")]);

    expect(screen.getByRole("button", { name: /3 steps/ })).toBeInTheDocument();
    expect(screen.queryByText("manual started")).toBeNull();
  });

  it("reveals the detail rows on expand and hides them again", () => {
    renderFold([ev("d1", "manual started"), ev("d2", "manual completed")]);

    fireEvent.click(screen.getByRole("button"));
    expect(screen.getByText("manual started")).toBeInTheDocument();
    expect(screen.getByText("manual completed")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button"));
    expect(screen.queryByText("manual started")).toBeNull();
  });
});
