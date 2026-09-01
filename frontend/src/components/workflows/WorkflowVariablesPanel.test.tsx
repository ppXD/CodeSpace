import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { WorkflowVariablesPanel } from "./WorkflowVariablesPanel";

describe("WorkflowVariablesPanel schema patches", () => {
  it("changes only type and preserves every schema keyword the dropdown does not own", () => {
    const onChange = vi.fn();
    render(
      <WorkflowVariablesPanel
        kind="inputs"
        items={[{
          name: "target",
          schema: { type: "string", description: "planner-owned", format: "uri", "x-plugin": { version: 2 } },
          required: false,
        }] as never}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "boolean" } });

    const saved = onChange.mock.calls.at(-1)![0][0];
    expect(saved.schema).toEqual({
      type: "boolean",
      description: "planner-owned",
      format: "uri",
      "x-plugin": { version: 2 },
    });
  });
});
