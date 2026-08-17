import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AddInputFieldModal } from "./AddInputFieldModal";

/**
 * A Select field with no options used to save as a bare {type:string} (no enum), which reopened as a plain
 * Text field — silently dropping the operator's type choice. The modal now requires at least one option before
 * a Select can be saved, so the type always survives the round-trip.
 */
describe("AddInputFieldModal — a Select requires an option", () => {
  it("blocks Save (with a hint) for a Select with no options, and enables it once one is added", () => {
    render(<AddInputFieldModal takenNames={[]} onSave={vi.fn()} onClose={vi.fn()} />);

    // Give it a valid name, then switch the type to Select (the type picker is the only combobox at first —
    // a Text field's default is a textbox).
    fireEvent.change(screen.getByPlaceholderText("e.g. start_time"), { target: { value: "choice" } });
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "select" } });

    // No option yet → Save is blocked with the hint.
    expect(screen.getByText("Add at least one option")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();

    // Add and fill one option → the hint clears and Save enables.
    fireEvent.click(screen.getByRole("button", { name: /Add option/i }));
    fireEvent.change(screen.getByPlaceholderText("Option 1"), { target: { value: "Yes" } });

    expect(screen.queryByText("Add at least one option")).toBeNull();
    expect(screen.getByRole("button", { name: "Save" })).toBeEnabled();
  });

  it("saves a Select's enum so it round-trips as a Select, not Text", () => {
    const onSave = vi.fn();
    render(<AddInputFieldModal takenNames={[]} onSave={onSave} onClose={vi.fn()} />);

    fireEvent.change(screen.getByPlaceholderText("e.g. start_time"), { target: { value: "choice" } });
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "select" } });
    fireEvent.click(screen.getByRole("button", { name: /Add option/i }));
    fireEvent.change(screen.getByPlaceholderText("Option 1"), { target: { value: "Yes" } });

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    const saved = onSave.mock.calls.at(-1)![0];
    expect((saved.schema as Record<string, unknown>).enum).toEqual(["Yes"]);   // enum present → reopens as Select
  });

  it("keeps a stored default this editor cannot represent", () => {
    // The plan projector writes an array of subtask objects as a field's default. None of this modal's
    // default controls can hold that, and resolveDefault answers `undefined` for it — so a save that
    // wrote its answer unconditionally deleted the operator's whole plan. Untouched means untouched.
    const onSave = vi.fn();
    const stored = {
      name: "subtasks",
      label: "Subtasks",
      description: "The planned subtasks the workflow fans out over.",
      schema: { type: "array", items: { type: "object" } },
      default: [{ title: "Add the migration" }, { title: "Wire the handler" }],
      required: false,
    } as never;

    render(<AddInputFieldModal initial={stored} takenNames={[]} onSave={onSave} onClose={vi.fn()} />);

    // Edit something else entirely.
    fireEvent.change(screen.getByPlaceholderText("subtasks"), { target: { value: "Planned subtasks" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    const saved = onSave.mock.calls.at(-1)![0];
    expect(saved.default).toEqual([{ title: "Add the migration" }, { title: "Wire the handler" }]);
    expect(saved.description).toBe("The planned subtasks the workflow fans out over.");
  });

  it("writes a default the operator did change", () => {
    // The other half — the preserve rule must not make the control one-way.
    const onSave = vi.fn();
    render(<AddInputFieldModal takenNames={[]} onSave={onSave} onClose={vi.fn()} />);

    fireEvent.change(screen.getByPlaceholderText("e.g. start_time"), { target: { value: "greeting" } });
    fireEvent.change(screen.getByPlaceholderText(/used when the runner leaves it blank/), { target: { value: "hello" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(onSave.mock.calls.at(-1)![0].default).toBe("hello");
  });
});
