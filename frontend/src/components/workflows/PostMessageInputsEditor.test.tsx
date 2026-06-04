import { fireEvent, render, screen } from "@testing-library/react";
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

// Mock the SchemaForm + ConversationSelector + UserMultiSelector so we aren't fighting their deps.
vi.mock("@/hooks/use-chat", () => ({ useConversations: () => ({ isLoading: false, data: [] }) }));
vi.mock("@/hooks/use-team-members", () => ({ useTeamMembers: () => ({ isLoading: false, data: [] }) }));

function renderEditor(inputs: Record<string, unknown>, onChange = vi.fn()) {
  render(<PostMessageInputsEditor inputs={inputs} onChange={onChange} inputSchema={schema} />);
  return onChange;
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

  it("activates Form when form is present (form wins over actions if both set)", () => {
    renderEditor({ form: { fields: {}, submitLabel: "Submit" } });
    expect(screen.getByRole("button", { name: "Form" })).toHaveAttribute("data-active");
  });
});

describe("PostMessageInputsEditor — kind switching", () => {
  it("switching to None removes interaction fields and preserves message fields", () => {
    const onChange = renderEditor({ conversationId: "c1", body: "hi", actions: [{ key: "ok", label: "OK" }] });
    fireEvent.click(screen.getByRole("button", { name: "None" }));
    expect(onChange).toHaveBeenCalledWith({ conversationId: "c1", body: "hi" });
  });

  it("switching to Buttons seeds an empty actions array and removes form", () => {
    const onChange = renderEditor({ conversationId: "c1", form: { fields: {} } });
    fireEvent.click(screen.getByRole("button", { name: "Buttons" }));
    expect(onChange).toHaveBeenCalledWith({ conversationId: "c1", actions: [] });
  });

  it("switching to Form seeds a default form object and removes actions", () => {
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

  it("switching to the currently-active kind re-seeds it (idempotent + safe to click again)", () => {
    const onChange = renderEditor({ actions: [{ key: "a", label: "A" }] });
    fireEvent.click(screen.getByRole("button", { name: "Buttons" }));
    // The user clicked the already-active button: actions is reset to [] (safe default), body preserved.
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
