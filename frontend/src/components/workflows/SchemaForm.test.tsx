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

/**
 * A structured nested object (one that declares `properties`) renders as a recursive sub-form — one
 * control per nested property — not the raw-JSON fallback. This is what makes chat.post_message's
 * `resolve { mode, count }` a real select + number in the inspector (mode → first|quorum). A FREE-FORM
 * object (type object, no declared properties — e.g. a form's `fields` JSON Schema) still falls to JSON.
 */
describe("SchemaForm nested object", () => {
  const nestedSchema = {
    type: "object",
    properties: {
      resolve: {
        type: "object",
        properties: {
          mode: { type: "string", enum: ["first", "quorum"] },
          count: { type: "integer" },
        },
      },
    },
  };

  it("renders a nested object's properties as a sub-form (enum → select, integer → number)", () => {
    render(<SchemaForm schema={nestedSchema} value={{ resolve: { mode: "first", count: 2 } }} onChange={vi.fn()} />);
    const select = screen.getByRole("combobox");
    expect([...select.querySelectorAll("option")].map((o) => o.textContent)).toEqual(["first", "quorum"]);
    expect(screen.getByRole("spinbutton")).toHaveValue(2);
  });

  it("merges a nested edit back under the parent key (resolve.mode → quorum, count preserved)", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={nestedSchema} value={{ resolve: { mode: "first", count: 2 } }} onChange={onChange} />);
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "quorum" } });
    expect(onChange).toHaveBeenCalledWith({ resolve: { mode: "quorum", count: 2 } });
  });

  it("groups the sub-form (wf-form-nested) and starts blank when the nested value is absent", () => {
    const { container } = render(<SchemaForm schema={nestedSchema} value={{}} onChange={vi.fn()} />);
    expect(container.querySelector(".wf-form-nested")).not.toBeNull();
    expect(screen.getByRole("spinbutton")).toHaveValue(null);
  });

  it("still falls back to a raw-JSON textarea for a FREE-FORM object (no declared properties)", () => {
    const freeform = { type: "object", properties: { fields: { type: "object", description: "free-form" } } };
    render(<SchemaForm schema={freeform} value={{ fields: { a: 1 } }} onChange={vi.fn()} />);
    const textarea = screen.getByRole("textbox");
    expect(textarea.className).toContain("wf-form-textarea-mono");
    expect(textarea).toHaveValue(JSON.stringify({ a: 1 }, null, 2));
  });
});

/**
 * x-advanced tucks secondary fields under a collapsed "Advanced" disclosure so the common path stays light
 * (e.g. a button's key/label/style stay inline; requiresComment/resolvesWait/vetoes move to Advanced). Purely
 * presentational — the value shape and editing are unchanged.
 */
describe("SchemaForm advanced-field disclosure", () => {
  const advSchema = { type: "object", properties: { key: { type: "string" }, vetoes: { type: "boolean", "x-advanced": true } } };

  it("renders x-advanced fields inside a collapsed disclosure, primary fields outside it", () => {
    const { container } = render(<SchemaForm schema={advSchema} value={{}} onChange={vi.fn()} />);

    const details = container.querySelector("details.wf-form-advanced");
    expect(details).not.toBeNull();
    expect(details).not.toHaveAttribute("open");                      // collapsed by default
    expect(details!.textContent).toContain("Vetoes");                 // advanced field tucked inside
    expect(screen.getByText("Key").closest("details")).toBeNull();    // primary field stays out of the disclosure
  });

  it("still edits an advanced field's value (children stay mounted under <details>)", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={advSchema} value={{}} onChange={onChange} />);
    fireEvent.click(screen.getByRole("checkbox"));
    expect(onChange).toHaveBeenCalledWith({ vetoes: true });
  });

  it("renders a plain form with no disclosure when nothing is x-advanced", () => {
    const { container } = render(<SchemaForm schema={{ type: "object", properties: { key: { type: "string" } } }} value={{}} onChange={vi.fn()} />);
    expect(container.querySelector("details.wf-form-advanced")).toBeNull();
  });
});

/**
 * Plain-language surface: `title` overrides the humanized property label, and `x-enumLabels` gives an enum
 * friendly option text — while the stored value stays the raw enum value (purely a display nicety).
 */
describe("SchemaForm title + enum labels", () => {
  it("uses `title` as the field label instead of the humanized property name", () => {
    render(<SchemaForm schema={{ type: "object", properties: { resolve: { type: "string", title: "Decision rule" } } }} value={{}} onChange={vi.fn()} />);
    expect(screen.getByText("Decision rule")).toBeInTheDocument();
    expect(screen.queryByText("Resolve")).toBeNull();
  });

  it("renders friendly x-enumLabels text but stores the raw enum value", () => {
    const onChange = vi.fn();
    const schema = { type: "object", properties: { mode: { type: "string", enum: ["first", "quorum"], "x-enumLabels": { first: "First response wins", quorum: "Quorum — N of the same" } } } };
    render(<SchemaForm schema={schema} value={{ mode: "first" }} onChange={onChange} />);

    const select = screen.getByRole("combobox");
    expect([...select.querySelectorAll("option")].map(o => o.textContent)).toEqual(["First response wins", "Quorum — N of the same"]);

    fireEvent.change(select, { target: { value: "quorum" } });
    expect(onChange).toHaveBeenCalledWith({ mode: "quorum" });   // the raw value is stored, not the label
  });
});

/**
 * An enum field is bind-able to a dynamic {{ref}} in the editor via the Pick ⇄ Expression toggle (the same
 * one string selectors get) — so e.g. git.pr_review.verdict can be wired to a chat card's clicked action,
 * not just a static option. Without suggestions (the run form) it stays a plain dropdown.
 */
describe("SchemaForm enum dual-mode", () => {
  const enumSchema = { type: "object", properties: { verdict: { type: "string", enum: ["approve", "request_changes"] } } };
  const suggestions = [{ path: "nodes.w.outputs.action", label: "nodes.w.outputs.action", category: "node" as const }];

  it("offers a Pick ⇄ Expression toggle on an enum and defaults to the dropdown for a literal value", () => {
    render(<SchemaForm schema={enumSchema} value={{ verdict: "approve" }} onChange={vi.fn()} variableSuggestions={suggestions} />);
    expect(screen.getByRole("button", { name: "Pick" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Expression" })).toBeInTheDocument();
    expect(screen.getByRole("combobox")).toBeInTheDocument();
  });

  it("opens an enum in Expression mode when the value is already a {{ref}} (no dropdown)", () => {
    render(<SchemaForm schema={enumSchema} value={{ verdict: "{{nodes.w.outputs.action}}" }} onChange={vi.fn()} variableSuggestions={suggestions} />);
    expect(screen.getByRole("button", { name: "Expression" }).getAttribute("data-active")).toBe("true");
    expect(screen.queryByRole("combobox")).toBeNull();
  });

  it("keeps an enum a plain dropdown with no toggle in the run form (no suggestions)", () => {
    render(<SchemaForm schema={enumSchema} value={{ verdict: "approve" }} onChange={vi.fn()} />);
    expect(screen.queryByRole("button", { name: "Pick" })).toBeNull();
    expect(screen.getByRole("combobox")).toBeInTheDocument();
  });
});
