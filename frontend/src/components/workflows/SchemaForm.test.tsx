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

// CredentialedModelSelector reads useCredentialedModels; ModelCredentialSelector reads useModelCredentials.
vi.mock("@/hooks/use-model-credentials", () => ({
  useCredentialedModels: () => ({ isLoading: false, data: [
    { rowId: "r1", modelId: "claude-opus-4-8", credentialId: "c1", credentialName: "Team Anthropic", provider: "Anthropic", tier: "Frontier", available: true },
  ] }),
  useModelCredentials: () => ({ isLoading: false, data: [
    { id: "c1", provider: "Anthropic", displayName: "Team Anthropic", status: "Active" },
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
    expect(screen.getByRole("textbox", { name: "Pick a conversation…" })).toBeInTheDocument();   // the conversation combobox
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
    expect(screen.getByRole("textbox", { name: "Pick a conversation…" })).toBeInTheDocument();
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
 * The supervisor's brain (`supervisorModelId`), reviewer (`reviewerModelId`) and dispatch allow-list
 * (`allowedModelIds`) declare `"x-selector": "credentialedModel"` — a key that was UNREGISTERED, so the
 * field fell through to a raw UUID text box. Now: scalar → a named model picker (dual-mode bindable),
 * array → a searchable multi-picker; the owning credential uses `"x-selector": "modelCredential"`. Here we
 * only prove SchemaForm wires them (rich behaviour lives in selectors/CredentialedModelSelector.test.tsx).
 */
describe("SchemaForm credentialed-model selector", () => {
  const brainSchema = { type: "object", properties: { supervisorModelId: { type: "string", "x-selector": "credentialedModel" } } };
  const poolSchema = { type: "object", properties: { allowedModelIds: { type: "array", items: { type: "string" }, "x-selector": "credentialedModel" } } };
  const credSchema = { type: "object", properties: { modelCredentialId: { type: "string", "x-selector": "modelCredential" } } };

  it("renders a searchable model picker for a scalar field, not a raw UUID text box", () => {
    render(<SchemaForm schema={brainSchema} value={{ supervisorModelId: "" }} onChange={vi.fn()} />);
    fireEvent.focus(screen.getByRole("textbox", { name: "Pick a model…" }));   // the combobox search, not a free-text uuid
    expect(screen.getByRole("option", { name: /claude-opus-4-8/ })).toBeInTheDocument();   // a picker of real models
  });

  it("stays dual-mode bindable in the editor (Pick ⇄ Expression)", () => {
    const suggestions = [{ path: "trigger.modelId", label: "trigger.modelId", category: "trigger" as const }];
    render(<SchemaForm schema={brainSchema} value={{ supervisorModelId: "" }} onChange={vi.fn()} variableSuggestions={suggestions} />);
    expect(screen.getByRole("button", { name: "Pick" })).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: "Pick a model…" })).toBeInTheDocument();   // Pick mode = the search combobox
  });

  it("renders a searchable multi-picker for an array field (allowedModelIds)", () => {
    render(<SchemaForm schema={poolSchema} value={{ allowedModelIds: ["r1"] }} onChange={vi.fn()} />);
    expect(screen.getByRole("button", { name: "Remove claude-opus-4-8" })).toBeInTheDocument();   // chip shows the model id
  });

  it("renders the credential picker for a modelCredential field", () => {
    render(<SchemaForm schema={credSchema} value={{ modelCredentialId: "" }} onChange={vi.fn()} />);
    fireEvent.focus(screen.getByRole("textbox", { name: "Team / operator default" }));
    expect(screen.getByRole("option", { name: /Team Anthropic/ })).toBeInTheDocument();
  });

  // The "on-disk value shape unchanged" contract rests on empty → absent-key coercion. Clearing a scalar
  // model emits undefined (the key drops from the persisted object), matching every other scalar selector.
  it("clears a scalar model field to an absent key when set to empty", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={brainSchema} value={{ supervisorModelId: "r1" }} onChange={onChange} />);
    fireEvent.click(screen.getByRole("button", { name: "Remove claude-opus-4-8" }));   // clear the single chip
    expect(onChange).toHaveBeenCalledWith({ supervisorModelId: undefined });
  });

  // Removing the last allowed model drops the key entirely — absent means "any of the team's models",
  // so an emptied allow-list must NOT persist as [] (which the engine also treats as all, but the key
  // should match the pre-edit absent shape).
  it("drops the array model field to an absent key when the last selection is removed", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={poolSchema} value={{ allowedModelIds: ["r1"] }} onChange={onChange} />);
    fireEvent.click(screen.getByRole("button", { name: "Remove claude-opus-4-8" }));
    expect(onChange).toHaveBeenCalledWith({ allowedModelIds: undefined });
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

describe("SchemaForm — x-showWhen conditional visibility", () => {
  const conditional = {
    type: "object",
    properties: {
      mode: { type: "string", enum: ["first", "quorum"] },
      count: { type: "integer", title: "Responders needed", "x-showWhen": { field: "mode", equals: "quorum" } },
    },
  };

  it("hides a field whose sibling condition is not met", () => {
    // count is irrelevant when mode is "first" → it must not render.
    render(<SchemaForm schema={conditional} value={{ mode: "first" }} onChange={vi.fn()} />);
    expect(screen.queryByText("Responders needed")).toBeNull();
  });

  it("shows the field once the sibling condition is met", () => {
    render(<SchemaForm schema={conditional} value={{ mode: "quorum" }} onChange={vi.fn()} />);
    expect(screen.getByText("Responders needed")).toBeInTheDocument();
  });

  it("shows fields with no x-showWhen unconditionally", () => {
    render(<SchemaForm schema={conditional} value={{ mode: "first" }} onChange={vi.fn()} />);
    expect(screen.getByText("Mode")).toBeInTheDocument();
  });
});

/**
 * When a field declares `x-group`, SchemaForm renders titled sections (ordered by the root `x-sections`)
 * instead of one flat list — the "narrow before you show" layout. Ungrouped fields fall into a trailing
 * "More" section; x-advanced fields keep their per-section Advanced drawer. A schema with NO x-group renders
 * exactly as before (backward-compatible), which is what every other test in this file already exercises.
 */
describe("SchemaForm grouped layout (x-group)", () => {
  const grouped = {
    type: "object",
    "x-sections": ["Limits", "Task"],
    properties: {
      goal: { type: "string", "x-group": "Task", title: "Goal" },        // Task field appears first in properties…
      budget: { type: "number", "x-group": "Limits", title: "Budget" },
      timeout: { type: "integer", "x-group": "Limits", title: "Timeout", "x-advanced": true },
      note: { type: "string", title: "Note" },                            // ungrouped → trailing "More"
    },
  };

  it("renders titled sections in x-sections order, ungrouped fields under 'More'", () => {
    render(<SchemaForm schema={grouped} value={{}} onChange={vi.fn()} />);
    const limits = screen.getByText("Limits");
    const task = screen.getByText("Task");
    const more = screen.getByText("More");

    // "Limits" is listed first in x-sections, so it renders before "Task" even though a Task field is first
    // in `properties`; the ungrouped "More" section trails.
    expect(limits.compareDocumentPosition(task) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(task.compareDocumentPosition(more) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("keeps x-advanced fields under an Advanced drawer inside their section", () => {
    render(<SchemaForm schema={grouped} value={{}} onChange={vi.fn()} />);
    expect(screen.getByText("Budget")).toBeInTheDocument();   // non-advanced, visible inline
    expect(screen.getByText("Advanced")).toBeInTheDocument(); // the Limits section's drawer holds Timeout
  });

  it("stays a flat form (no section headers) when no field declares a group", () => {
    const flat = { type: "object", properties: { a: { type: "string", title: "A" }, b: { type: "string", title: "B" } } };
    const { container } = render(<SchemaForm schema={flat} value={{}} onChange={vi.fn()} />);
    expect(container.querySelector(".wf-form-group")).toBeNull();
    expect(screen.getByText("A")).toBeInTheDocument();
  });
});

/**
 * x-control: "segmented" — the FIRST widget through the new x-control discriminator. An enum renders as
 * a lifted button-group instead of a <select>, storing the RAW enum value as a string (byte-identical to
 * the <select> path), and falls through to the <select> whenever x-control is absent or unrecognised.
 */
describe("SchemaForm x-control: segmented", () => {
  const segSchema = (control?: unknown) => ({
    type: "object",
    properties: {
      mode: {
        type: "string",
        enum: ["a", "b", "c"],
        "x-control": control,
        "x-enumLabels": { a: "Alpha", b: "Bravo", c: "Charlie" },
      },
    },
  });

  it("renders enum options as a segmented button-group (not a <select>) with friendly labels", () => {
    render(<SchemaForm schema={segSchema("segmented")} value={{ mode: "b" }} onChange={vi.fn()} />);
    expect(screen.queryByRole("combobox")).toBeNull();                           // no <select>
    const group = screen.getByRole("radiogroup");
    expect(group.classList.contains("wf-segmented")).toBe(true);
    expect(screen.getByRole("radio", { name: "Bravo" }).getAttribute("aria-checked")).toBe("true");
    expect(screen.getByRole("radio", { name: "Alpha" }).getAttribute("aria-checked")).toBe("false");
  });

  it("stores the raw enum value as a string on click — byte-identical to the <select> path", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={segSchema("segmented")} value={{ mode: "a" }} onChange={onChange} />);
    fireEvent.click(screen.getByRole("radio", { name: "Charlie" }));
    expect(onChange).toHaveBeenCalledWith({ mode: "c" });                        // "c", exactly like <select>
  });

  it("is a single tab stop with a roving tabindex (only the checked radio is tabbable)", () => {
    render(<SchemaForm schema={segSchema("segmented")} value={{ mode: "b" }} onChange={vi.fn()} />);
    expect(screen.getByRole("radio", { name: "Bravo" }).getAttribute("tabindex")).toBe("0");
    expect(screen.getByRole("radio", { name: "Alpha" }).getAttribute("tabindex")).toBe("-1");
    expect(screen.getByRole("radio", { name: "Charlie" }).getAttribute("tabindex")).toBe("-1");
  });

  it("moves the selection with arrow keys, wrapping at the ends", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={segSchema("segmented")} value={{ mode: "c" }} onChange={onChange} />);
    fireEvent.keyDown(screen.getByRole("radiogroup"), { key: "ArrowRight" });   // c -> wrap to a
    expect(onChange).toHaveBeenLastCalledWith({ mode: "a" });
    fireEvent.keyDown(screen.getByRole("radiogroup"), { key: "Home" });         // -> a
    expect(onChange).toHaveBeenLastCalledWith({ mode: "a" });
    fireEvent.keyDown(screen.getByRole("radiogroup"), { key: "End" });          // -> c
    expect(onChange).toHaveBeenLastCalledWith({ mode: "c" });
  });

  it("falls through to the <select> when x-control is absent (non-breaking default)", () => {
    render(<SchemaForm schema={segSchema(undefined)} value={{ mode: "a" }} onChange={vi.fn()} />);
    expect(screen.getByRole("combobox")).toBeTruthy();
    expect(screen.queryByRole("radiogroup")).toBeNull();
  });

  it("falls through to the <select> for an unrecognised x-control value", () => {
    render(<SchemaForm schema={segSchema("bogus")} value={{ mode: "a" }} onChange={vi.fn()} />);
    expect(screen.getByRole("combobox")).toBeTruthy();
    expect(screen.queryByRole("radiogroup")).toBeNull();
  });
});

/**
 * x-control: "stepper" — a bounded integer as a −/+ control. Clamps to the schema min/max, shows the
 * effective default as placeholder when empty, stores a bare number, and falls through to the plain
 * number input when x-control is absent.
 */
describe("SchemaForm x-control: stepper", () => {
  const stepSchema = (control: unknown = "stepper") => ({
    type: "object",
    properties: {
      parallelism: { type: "integer", minimum: 1, maximum: 4, default: 2, "x-control": control, "x-unit": "×" },
    },
  });

  it("renders a −/+ stepper with the default shown as placeholder (never a required-looking blank)", () => {
    render(<SchemaForm schema={stepSchema()} value={{}} onChange={vi.fn()} />);
    expect(screen.getByRole("button", { name: "Decrease" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Increase" })).toBeTruthy();
    const input = screen.getByRole("textbox") as HTMLInputElement;
    expect(input.getAttribute("placeholder")).toBe("2");   // default (unit shows in its own span)
    expect(input.value).toBe("");
    expect(screen.getByText("×")).toBeTruthy();             // the x-unit label
  });

  it("steps up by one and stores a number", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={stepSchema()} value={{ parallelism: 2 }} onChange={onChange} />);
    fireEvent.click(screen.getByRole("button", { name: "Increase" }));
    expect(onChange).toHaveBeenCalledWith({ parallelism: 3 });
  });

  it("steps up from the default when the value is empty", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={stepSchema()} value={{}} onChange={onChange} />);
    fireEvent.click(screen.getByRole("button", { name: "Increase" }));
    expect(onChange).toHaveBeenCalledWith({ parallelism: 3 });   // 2 (default) + 1
  });

  it("disables Increase at max and Decrease at min", () => {
    const { rerender } = render(<SchemaForm schema={stepSchema()} value={{ parallelism: 4 }} onChange={vi.fn()} />);
    expect(screen.getByRole("button", { name: "Increase" })).toBeDisabled();
    rerender(<SchemaForm schema={stepSchema()} value={{ parallelism: 1 }} onChange={vi.fn()} />);
    expect(screen.getByRole("button", { name: "Decrease" })).toBeDisabled();
  });

  it("clamps a typed value into range, and clears to undefined when emptied", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={stepSchema()} value={{ parallelism: 2 }} onChange={onChange} />);
    fireEvent.change(screen.getByRole("textbox"), { target: { value: "9" } });
    expect(onChange).toHaveBeenLastCalledWith({ parallelism: 4 });   // clamped to max, numeric
    fireEvent.change(screen.getByRole("textbox"), { target: { value: "" } });
    expect(onChange).toHaveBeenLastCalledWith({ parallelism: undefined });
  });

  it("falls through to the plain number input when x-control is absent", () => {
    const plain = { type: "object", properties: { parallelism: { type: "integer", minimum: 1, maximum: 4, default: 2 } } };
    render(<SchemaForm schema={plain} value={{}} onChange={vi.fn()} />);
    expect(screen.getByRole("spinbutton")).toBeTruthy();          // <input type="number">
    expect(screen.queryByRole("button", { name: "Increase" })).toBeNull();
  });
});

/**
 * An array of strings renders as removable token chips (replacing the comma-separated box). Each entry is
 * a chip with a remove button; the trailing input adds more (Enter / comma commits, Backspace removes the
 * last), de-dupes, and offers items.enum as autocomplete. Stores the same string[].
 */
describe("SchemaForm array of string → chips", () => {
  const chipSchema = (items: Record<string, unknown> = {}) => ({
    type: "object",
    properties: { labels: { type: "array", items: { type: "string", ...items } } },
  });

  it("renders each string as a removable chip, not a comma box", () => {
    const { container } = render(<SchemaForm schema={chipSchema()} value={{ labels: ["bug", "urgent"] }} onChange={vi.fn()} />);
    const chips = container.querySelectorAll(".wf-chip");
    expect(chips).toHaveLength(2);
    expect(chips[0].textContent).toContain("bug");
    expect(screen.getByRole("button", { name: "Remove urgent" })).toBeTruthy();
    expect((screen.getByRole("textbox") as HTMLInputElement).value).toBe("");   // empty add-input, not "bug, urgent"
  });

  it("adds a token on Enter and stores the string array", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={chipSchema()} value={{ labels: ["bug"] }} onChange={onChange} />);
    const input = screen.getByRole("textbox");
    fireEvent.change(input, { target: { value: "urgent" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onChange).toHaveBeenCalledWith({ labels: ["bug", "urgent"] });
  });

  it("commits on a trailing comma and de-dupes existing tokens", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={chipSchema()} value={{ labels: ["bug"] }} onChange={onChange} />);
    const input = screen.getByRole("textbox");
    fireEvent.change(input, { target: { value: "bug," } });        // duplicate → dropped
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.change(input, { target: { value: "new," } });
    expect(onChange).toHaveBeenCalledWith({ labels: ["bug", "new"] });
  });

  it("removes a chip via its × button", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={chipSchema()} value={{ labels: ["bug", "urgent"] }} onChange={onChange} />);
    fireEvent.click(screen.getByRole("button", { name: "Remove bug" }));
    expect(onChange).toHaveBeenCalledWith({ labels: ["urgent"] });
  });

  it("removes the last chip on Backspace when the input is empty", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={chipSchema()} value={{ labels: ["a", "b"] }} onChange={onChange} />);
    fireEvent.keyDown(screen.getByRole("textbox"), { key: "Backspace" });
    expect(onChange).toHaveBeenCalledWith({ labels: ["a"] });
  });

  it("offers items.enum values as autocomplete, minus the ones already chosen", () => {
    const { container } = render(<SchemaForm schema={chipSchema({ enum: ["mon", "tue", "wed"] })} value={{ labels: ["mon"] }} onChange={vi.fn()} />);
    const dl = container.querySelector("datalist");
    expect(dl).not.toBeNull();
    const opts = Array.from(dl!.querySelectorAll("option")).map((o) => o.getAttribute("value"));
    expect(opts).toEqual(["tue", "wed"]);
  });
});

/**
 * x-control: "radioCards" — a closed enum as stacked cards, each with a friendly label (x-enumLabels) and a
 * one-line consequence (x-optionConsequence). Stores the raw enum value as a string; keyboard-navigable as a
 * radiogroup; falls through to the <select> when x-control is absent or unrecognised.
 */
describe("SchemaForm x-control: radioCards", () => {
  const rcSchema = (control: unknown = "radioCards") => ({
    type: "object",
    properties: {
      reviewMode: {
        type: "integer", enum: [0, 1, 2], "x-control": control,
        "x-enumLabels": { 0: "Skip review", 1: "Gate", 2: "Improve" },
        "x-optionConsequence": { 0: "runs without a check", 1: "parks until you approve", 2: "auto-revises once" },
      },
    },
  });

  it("renders stacked cards with a label and a consequence line, not a <select>", () => {
    render(<SchemaForm schema={rcSchema()} value={{ reviewMode: 1 }} onChange={vi.fn()} />);
    expect(screen.queryByRole("combobox")).toBeNull();
    expect(screen.getByRole("radiogroup").classList.contains("wf-radiocards")).toBe(true);
    expect(screen.getByText("Gate")).toBeTruthy();
    expect(screen.getByText("parks until you approve")).toBeTruthy();
    expect(screen.getByRole("radio", { name: /Gate/ }).getAttribute("aria-checked")).toBe("true");
  });

  it("stores the raw enum value as a string on click", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={rcSchema()} value={{ reviewMode: 0 }} onChange={onChange} />);
    fireEvent.click(screen.getByRole("radio", { name: /Improve/ }));
    expect(onChange).toHaveBeenCalledWith({ reviewMode: "2" });
  });

  it("moves the selection with arrow keys and keeps a single tab stop", () => {
    const onChange = vi.fn();
    render(<SchemaForm schema={rcSchema()} value={{ reviewMode: 2 }} onChange={onChange} />);
    fireEvent.keyDown(screen.getByRole("radiogroup"), { key: "ArrowRight" });   // 2 → wrap to 0
    expect(onChange).toHaveBeenCalledWith({ reviewMode: "0" });
    expect(screen.getByRole("radio", { name: /Improve/ }).getAttribute("tabindex")).toBe("0");
    expect(screen.getByRole("radio", { name: /Skip review/ }).getAttribute("tabindex")).toBe("-1");
  });

  it("falls through to the <select> when x-control is absent", () => {
    const plain = { type: "object", properties: { reviewMode: { type: "integer", enum: [0, 1, 2], "x-enumLabels": { 0: "Off", 1: "Gate", 2: "Improve" } } } };
    render(<SchemaForm schema={plain} value={{ reviewMode: 0 }} onChange={vi.fn()} />);
    expect(screen.getByRole("combobox")).toBeTruthy();
    expect(screen.queryByRole("radiogroup")).toBeNull();
  });

  it("falls through to the <select> for an unrecognised x-control value", () => {
    render(<SchemaForm schema={rcSchema("bogus")} value={{ reviewMode: 0 }} onChange={vi.fn()} />);
    expect(screen.getByRole("combobox")).toBeTruthy();
    expect(screen.queryByRole("radiogroup")).toBeNull();
  });
});
