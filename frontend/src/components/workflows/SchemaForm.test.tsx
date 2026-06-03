import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { SchemaForm } from "./SchemaForm";

// ConversationSelector (pick mode of the dual-mode selector) reads useConversations.
vi.mock("@/hooks/use-chat", () => ({
  useConversations: () => ({ isLoading: false, data: [{ id: "c1", kind: "Channel", slug: "general", name: "General" }] }),
}));

// UserMultiSelector reads useTeamMembers.
vi.mock("@/hooks/use-team-members", () => ({
  useTeamMembers: () => ({ isLoading: false, data: [
    { userId: "u1", name: "Alice", email: "a@x", avatarUrl: null, isBot: false },
    { userId: "u2", name: "Bob", email: "b@x", avatarUrl: null, isBot: false },
  ] }),
}));

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

  it("renders a boolean sub-field as an inline checkbox-before-label, not a stacked row", () => {
    const onChange = renderForm({ buttons: [{ kind: "approve" }] });

    const checkbox = screen.getByRole("checkbox");
    const row = checkbox.closest(".wf-form-check");
    expect(row).not.toBeNull();                  // inline .wf-form-check, not the label-on-top .wf-form-row
    expect(row!.textContent).toContain("Urgent"); // label sits beside the checkbox, same row

    fireEvent.click(checkbox);
    expect(onChange).toHaveBeenCalledWith({ buttons: [{ kind: "approve", urgent: true }] });
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

/**
 * A scalar-string `x-selector` field (here `conversation`) is dynamically bindable: in the editor
 * (variable suggestions present) it offers a Pick ⇄ Expression toggle so the value can be a static
 * pick OR a `{{ }}` reference. Without suggestions (the run form) it stays a plain picker.
 */
describe("SchemaForm scalar selector dual-mode", () => {
  const convSchema = { type: "object", properties: { conversationId: { type: "string", "x-selector": "conversation" } } };
  const suggestions = [{ path: "trigger.channelId", label: "trigger.channelId", category: "trigger" as const }];

  function renderConv(value: unknown, withSuggestions: boolean) {
    render(<SchemaForm schema={convSchema} value={value} onChange={vi.fn()} variableSuggestions={withSuggestions ? suggestions : undefined} />);
  }

  it("offers a Pick ⇄ Expression toggle and defaults to the picker", () => {
    renderConv({ conversationId: "" }, true);
    expect(screen.getByRole("button", { name: "Pick" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Expression" })).toBeInTheDocument();
    expect(screen.getByRole("combobox")).toBeInTheDocument();   // the conversation dropdown
  });

  it("switches to the expression input when Expression is chosen", () => {
    renderConv({ conversationId: "" }, true);
    fireEvent.click(screen.getByRole("button", { name: "Expression" }));
    expect(screen.queryByRole("combobox")).toBeNull();   // dropdown replaced by the @/{{ }} input
  });

  it("opens in expression mode when the value is already a reference", () => {
    renderConv({ conversationId: "{{trigger.channelId}}" }, true);
    expect(screen.getByRole("button", { name: "Expression" }).getAttribute("data-active")).toBe("true");
    expect(screen.queryByRole("combobox")).toBeNull();
  });

  it("stays a plain picker with no toggle when there are no variable suggestions (run form)", () => {
    renderConv({ conversationId: "" }, false);
    expect(screen.queryByRole("button", { name: "Pick" })).toBeNull();
    expect(screen.getByRole("combobox")).toBeInTheDocument();
  });
});

/**
 * An array field with `"x-selector": "user"` (e.g. allowedResponderUserIds) renders a SEARCHABLE
 * combobox — selected members as removable tags + a filtered dropdown — not one chip per member, so it
 * scales to large teams. The stored value is an array of user ids. (Rich picker behaviours — filter, cap,
 * keyboard, remove — live in selectors/UserSelector.test.tsx; here we only prove SchemaForm wires the array.)
 */
describe("SchemaForm user multi-selector", () => {
  const userSchema = { type: "object", properties: { allowed: { type: "array", items: { type: "string", format: "uuid" }, "x-selector": "user" } } };

  it("renders a removable tag for each selected member, not an inline list of the rest", () => {
    render(<SchemaForm schema={userSchema} value={{ allowed: ["u1"] }} onChange={vi.fn()} />);
    expect(screen.getByRole("button", { name: "Remove Alice" })).toBeInTheDocument();
    expect(screen.queryByText("Bob")).toBeNull();   // unselected members appear only when you search
  });

  it("adds a member to the allowlist by picking from the search dropdown (array of ids)", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={userSchema} value={{ allowed: ["u1"] }} onChange={onChange} />);
    fireEvent.focus(screen.getByRole("textbox", { name: "Search members" }));
    fireEvent.mouseDown(screen.getByRole("option", { name: /Bob/ }));
    expect(onChange).toHaveBeenCalledWith({ allowed: ["u1", "u2"] });
  });
});
