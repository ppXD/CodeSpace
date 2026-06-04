import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";

import { PostMessageInputsEditor } from "./PostMessageInputsEditor";

// A realistic slice of chat.post_message InputSchema — enough to exercise all three zones
// and the type picker, without the full text of every description.
const schema = {
  type: "object",
  properties: {
    conversationId: { type: "string", "x-selector": "conversation" },
    body: { type: "string", minLength: 1 },
    actions: {
      type: "array",
      "x-interactionField": true,
      "x-interactionLabel": "Buttons",
      items: { type: "object", properties: { key: { type: "string" }, label: { type: "string" } }, required: ["key", "label"] },
    },
    form: {
      type: "object",
      "x-interactionField": true,
      "x-interactionLabel": "Form",
      properties: { fields: { type: "object" }, submitLabel: { type: "string" } },
      required: ["fields"],
    },
    allowedResponderUserIds: { type: "array", items: { type: "string" }, "x-selector": "user" },
  },
  required: ["conversationId", "body"],
};

// Mock the SchemaForm-internal selectors so we aren't fighting their data deps.
vi.mock("@/hooks/use-chat", () => ({ useConversations: () => ({ isLoading: false, data: [] }) }));
vi.mock("@/hooks/use-team-members", () => ({ useTeamMembers: () => ({ isLoading: false, data: [] }) }));

// Stateless render — the parent never re-feeds onChange's output back as `inputs`. Good for
// asserting a SINGLE onChange payload (the value the editor *would* commit on one click).
function renderEditor(inputs: Record<string, unknown>, onChange = vi.fn(), nodeId = "n1") {
  render(<PostMessageInputsEditor inputs={inputs} onChange={onChange} inputSchema={schema} nodeId={nodeId} />);
  return onChange;
}

// Stateful harness — mirrors the real inspector: onChange updates the controlled `inputs`, so
// multi-step flows (switch away, then back) behave like production. getInputs() reads the latest.
function renderStateful(initial: Record<string, unknown>, nodeId = "n1") {
  let latest = initial;
  function Harness() {
    const [val, setVal] = useState(initial);
    latest = val;
    return <PostMessageInputsEditor inputs={val} onChange={setVal} inputSchema={schema} nodeId={nodeId} />;
  }
  render(<Harness />);
  return () => latest;
}

describe("PostMessageInputsEditor — type picker options", () => {
  it("shows None + one button per x-interactionField in declaration order", () => {
    renderEditor({});
    expect(screen.getByRole("button", { name: "None" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Buttons" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Form" })).toBeInTheDocument();
  });

  it("activates None when no interaction field is set", () => {
    renderEditor({ conversationId: "c1", body: "hi" });
    expect(screen.getByRole("button", { name: "None" })).toHaveAttribute("data-active");
    expect(screen.getByRole("button", { name: "Buttons" })).not.toHaveAttribute("data-active");
  });

  it("activates Buttons when actions is present", () => {
    renderEditor({ actions: [{ key: "approve", label: "Approve" }] });
    expect(screen.getByRole("button", { name: "Buttons" })).toHaveAttribute("data-active");
  });

  it("activates Form when form is present", () => {
    renderEditor({ form: { fields: {}, submitLabel: "Submit" } });
    expect(screen.getByRole("button", { name: "Form" })).toHaveAttribute("data-active");
  });
});

describe("PostMessageInputsEditor — kind switching", () => {
  it("switching to None removes the live interaction field and preserves message fields", () => {
    const onChange = renderEditor({ conversationId: "c1", body: "hi", actions: [{ key: "ok", label: "OK" }] });
    fireEvent.click(screen.getByRole("button", { name: "None" }));
    expect(onChange).toHaveBeenCalledWith({ conversationId: "c1", body: "hi" });
  });

  it("switching to Buttons seeds an empty actions array and removes the live form", () => {
    const onChange = renderEditor({ conversationId: "c1", form: { fields: {} } });
    fireEvent.click(screen.getByRole("button", { name: "Buttons" }));
    expect(onChange).toHaveBeenCalledWith({ conversationId: "c1", actions: [] });
  });

  it("switching to Form seeds a default form object and removes the live actions", () => {
    const onChange = renderEditor({ conversationId: "c1", actions: [] });
    fireEvent.click(screen.getByRole("button", { name: "Form" }));
    const call = onChange.mock.calls.at(-1)![0];
    expect(call.actions).toBeUndefined();
    expect(call.form).toBeDefined();
    expect((call.form as Record<string, unknown>).submitLabel).toBe("Submit");
  });

  it("switching between kinds preserves non-interaction inputs (body, conversationId)", () => {
    const onChange = renderEditor({ conversationId: "conv-1", body: "my msg", actions: [] });
    fireEvent.click(screen.getByRole("button", { name: "Form" }));
    const call = onChange.mock.calls.at(-1)![0];
    expect(call.conversationId).toBe("conv-1");
    expect(call.body).toBe("my msg");
  });

  it("clicking the already-active kind preserves its data (no wipe)", () => {
    const onChange = renderEditor({ actions: [{ key: "a", label: "A" }] });
    fireEvent.click(screen.getByRole("button", { name: "Buttons" }));
    // Re-selecting the current kind must NOT reset it to the empty seed — the user's buttons stay.
    expect(onChange).toHaveBeenCalledWith({ actions: [{ key: "a", label: "A" }] });
  });
});

describe("PostMessageInputsEditor — session drafts (switching never destroys data)", () => {
  it("switch Buttons → Form → Buttons restores the original buttons; form is stashed not lost", () => {
    const get = renderStateful({ conversationId: "c", actions: [{ key: "approve", label: "Approve" }] });

    fireEvent.click(screen.getByRole("button", { name: "Form" }));
    expect(get().actions).toBeUndefined();          // not live while Form is selected…
    expect(get().form).toBeDefined();               // …Form is now the live kind

    fireEvent.click(screen.getByRole("button", { name: "Buttons" }));
    expect(get().actions).toEqual([{ key: "approve", label: "Approve" }]);  // original restored
    expect(get().form).toBeUndefined();             // Form value retained as a draft, just not live
    expect(get().conversationId).toBe("c");         // message fields untouched throughout
  });

  it("switch Buttons → None → Buttons restores the original buttons", () => {
    const get = renderStateful({ actions: [{ key: "ship", label: "Ship it" }] });

    fireEvent.click(screen.getByRole("button", { name: "None" }));
    expect(get().actions).toBeUndefined();          // plain message — nothing live

    fireEvent.click(screen.getByRole("button", { name: "Buttons" }));
    expect(get().actions).toEqual([{ key: "ship", label: "Ship it" }]);  // restored from draft
  });

  it("drafts do not leak between nodes — switching the inspector to a new node resets them", () => {
    const onChange = vi.fn();
    const { rerender } = render(
      <PostMessageInputsEditor inputs={{ actions: [{ key: "a", label: "A" }] }} onChange={onChange} inputSchema={schema} nodeId="n1" />,
    );

    // Stash node n1's buttons as a draft by switching it to None.
    fireEvent.click(screen.getByRole("button", { name: "None" }));

    // Inspector now points at a DIFFERENT node (n2) with empty inputs — same component instance.
    rerender(<PostMessageInputsEditor inputs={{}} onChange={onChange} inputSchema={schema} nodeId="n2" />);
    onChange.mockClear();

    // Selecting Buttons on n2 must seed a fresh empty array, NOT restore n1's stashed [{key:"a"}].
    fireEvent.click(screen.getByRole("button", { name: "Buttons" }));
    expect(onChange).toHaveBeenCalledWith({ actions: [] });
  });
});

describe("PostMessageInputsEditor — sub-editor visibility", () => {
  it("shows no sub-editor when kind is None", () => {
    renderEditor({});
    expect(screen.queryByRole("button", { name: /add item/i })).toBeNull();
  });

  it("shows the Buttons sub-editor (Add item) when kind is Buttons", () => {
    renderEditor({ actions: [] });
    expect(screen.getByRole("button", { name: /add item/i })).toBeInTheDocument();
  });
});
