import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { SchemaForm } from "./SchemaForm";

/**
 * Generic object-array editing. A property of `array` of `object` renders a repeatable list of
 * sub-forms (one per item), driven purely by `items.properties` — no per-node knowledge. The item
 * schema here uses enum + boolean fields so the rows are easy to drive without the contenteditable
 * string picker (covered elsewhere). Pins: row-per-item, add, remove (clears on last), edit-by-index.
 */
const schema = {
  type: "object",
  properties: {
    buttons: {
      type: "array",
      items: {
        type: "object",
        properties: {
          kind: { type: "string", enum: ["approve", "reject"] },
          urgent: { type: "boolean" },
        },
      },
    },
  },
};

function renderForm(value: unknown, onChange = vi.fn()) {
  render(<SchemaForm schema={schema} value={value} onChange={onChange} />);
  return onChange;
}

describe("SchemaForm object-array editor", () => {
  it("renders one sub-form row per item", () => {
    renderForm({ buttons: [{ kind: "approve" }, { kind: "reject" }] });
    expect(screen.getAllByRole("combobox")).toHaveLength(2);   // one enum select per row
  });

  it("appends a blank row on Add item", () => {
    const onChange = renderForm({ buttons: [{ kind: "approve" }] });
    fireEvent.click(screen.getByRole("button", { name: /add item/i }));
    expect(onChange).toHaveBeenCalledWith({ buttons: [{ kind: "approve" }, {}] });
  });

  it("removes the targeted row, clearing the field when the last row goes", () => {
    const onChange = renderForm({ buttons: [{ kind: "approve" }] });
    fireEvent.click(screen.getByRole("button", { name: "Remove item" }));
    expect(onChange.mock.calls.at(-1)![0].buttons).toBeUndefined();
  });

  it("edits the correct row by index, leaving siblings untouched", () => {
    const onChange = renderForm({ buttons: [{ kind: "approve" }, { kind: "approve" }] });
    fireEvent.change(screen.getAllByRole("combobox")[1], { target: { value: "reject" } });
    expect(onChange).toHaveBeenCalledWith({ buttons: [{ kind: "approve" }, { kind: "reject" }] });
  });

  it("starts from an empty list when the array value is absent", () => {
    const onChange = renderForm({});
    fireEvent.click(screen.getByRole("button", { name: /add item/i }));
    expect(onChange).toHaveBeenCalledWith({ buttons: [{}] });
  });
});
