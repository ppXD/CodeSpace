import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, renderHook, screen, waitFor, within } from "@testing-library/react";
import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ExternalTriggerInspector } from "./external-trigger-inspector";
import { ExternalTriggerEditor } from "./external-trigger-editor";
import { useUpdateWorkflow, useWorkflowExternalTriggers } from "@/hooks/use-workflows";

const REQUESTS = vi.fn<typeof fetch>();
const CONFIG_CHANGE = vi.fn();
const SUMMARY = { id: "trigger-1", workflowId: "workflow-1", nodeId: "ext", slug: "prod-alerts" };

function renderInspector(currentSlug: string | null = "prod-alerts", slug = "prod-alerts") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  function Inspector() {
    const [config, setConfig] = useState<Record<string, unknown>>({ slug, inputSchema: {} });
    return <ExternalTriggerInspector config={config} nodeId="ext" currentSlug={currentSlug} onConfigChange={(next) => { CONFIG_CHANGE(next); setConfig(next); }} />;
  }
  return render(<QueryClientProvider client={client}><Inspector /></QueryClientProvider>);
}

beforeEach(() => {
  CONFIG_CHANGE.mockReset();
  REQUESTS.mockReset();
  REQUESTS.mockImplementation(async (_url, init) => new Response(init?.method === "POST" && String(_url).endsWith("/rename") ? null : JSON.stringify({ key: "ext_secret" }), { status: init?.method === "POST" && String(_url).endsWith("/rename") ? 204 : 200 }));
  vi.stubGlobal("fetch", REQUESTS);
});

afterEach(() => vi.unstubAllGlobals());

describe("ExternalTriggerInspector", () => {
  it("preserves a boolean-false property when another property's label changes", () => {
    const client = new QueryClient();
    const config = { slug: "prod-alerts", inputSchema: { properties: { blocked: false, message: { type: "string" } } } };
    render(<QueryClientProvider client={client}><ExternalTriggerInspector config={config} nodeId="ext" currentSlug="prod-alerts" onConfigChange={CONFIG_CHANGE} /></QueryClientProvider>);
    fireEvent.click(screen.getByText("message"));
    fireEvent.change(screen.getByPlaceholderText("message"), { target: { value: "Alert message" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
    expect(CONFIG_CHANGE).toHaveBeenLastCalledWith({ slug: "prod-alerts", inputSchema: { type: "object", properties: { blocked: false, message: { type: "string", title: "Alert message" } }, required: [] } });
    expect(CONFIG_CHANGE.mock.lastCall?.[0].inputSchema.properties.blocked).toBe(false);
  });

  it("preserves a boolean constraint and warns when its display label cannot be applied", () => {
    const client = new QueryClient();
    const config = { slug: "prod-alerts", inputSchema: { properties: { blocked: false } } };
    render(<QueryClientProvider client={client}><ExternalTriggerInspector config={config} nodeId="ext" currentSlug="prod-alerts" onConfigChange={CONFIG_CHANGE} /></QueryClientProvider>);
    fireEvent.click(screen.getByText("blocked"));
    fireEvent.change(screen.getByPlaceholderText("blocked"), { target: { value: "Blocked field" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
    expect(CONFIG_CHANGE.mock.lastCall?.[0].inputSchema.properties.blocked).toBe(false);
    expect(screen.getByRole("alert")).toHaveTextContent(/blocked.*label.*default.*could not be applied.*constraint.*preserved/i);
  });

  it("shows the keyless canonical endpoint and only copies a key-bearing curl after reveal", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
    renderInspector();
    expect(screen.getByText("/api/ext/prod-alerts/run")).toBeInTheDocument();
    expect(REQUESTS).not.toHaveBeenCalled();
    expect(screen.queryByDisplayValue(/^ext_/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /show key/i }));
    expect(await screen.findByDisplayValue("ext_secret")).toBeInTheDocument();
    expect(REQUESTS).toHaveBeenCalledWith(expect.stringContaining("/prod-alerts/key"), expect.anything());
    fireEvent.click(screen.getByRole("button", { name: /copy key/i }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith("ext_secret"));
    fireEvent.click(screen.getByRole("button", { name: /copy curl/i }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(expect.stringContaining("/api/ext/prod-alerts/run?key=ext_secret")));
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining("curl -X POST"));
  });

  it("requires confirmation before rotating and warns that the old key stops working immediately", async () => {
    renderInspector();
    fireEvent.click(screen.getByRole("button", { name: /regenerate/i }));
    const group = screen.getByRole("group", { name: /confirm.*regenerate/i });
    expect(within(group).getByText(/old key stops working immediately/i)).toBeInTheDocument();
    expect(REQUESTS).not.toHaveBeenCalled();
    fireEvent.click(within(group).getByRole("button", { name: /cancel/i }));
    expect(REQUESTS).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: /regenerate/i }));
    fireEvent.click(within(screen.getByRole("group")).getByRole("button", { name: /confirm/i }));
    expect(await screen.findByDisplayValue("ext_secret")).toBeInTheDocument();
    expect(REQUESTS).toHaveBeenCalledWith(expect.stringContaining("/prod-alerts/rotate-key"), expect.objectContaining({ method: "POST" }));
  });

  it("keeps live and draft names distinct and explicitly confirms rename using desiredSlug", async () => {
    renderInspector("prod-alerts", "New Alerts");
    expect(screen.getByText("/api/ext/prod-alerts/run")).toBeInTheDocument();
    expect(screen.getByText(/saving.*does not rename/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /^rename/i }));
    const group = screen.getByRole("group", { name: /confirm.*rename/i });
    expect(within(group).getByText(/existing url will stop working/i)).toBeInTheDocument();
    expect(REQUESTS).not.toHaveBeenCalled();
    fireEvent.click(within(group).getByRole("button", { name: /confirm/i }));
    await waitFor(() => expect(REQUESTS).toHaveBeenCalledWith(expect.stringContaining("/prod-alerts/rename"), expect.objectContaining({ method: "POST", body: JSON.stringify({ desiredSlug: "new-alerts" }) })));
  });

  it("refuses an all-CJK draft without a fallback and disables rename", () => {
    renderInspector();
    fireEvent.change(screen.getByRole("textbox", { name: /draft slug/i }), { target: { value: "日本語" } });
    expect(screen.getByRole("alert")).toHaveTextContent(/use an english/i);
    expect(screen.getByRole("button", { name: /^rename/i })).toBeDisabled();
    expect(CONFIG_CHANGE).toHaveBeenLastCalledWith({ slug: "日本語", inputSchema: {} });
    expect(screen.getByText("/api/ext/prod-alerts/run")).toBeInTheDocument();
  });

  it("shows a save-first state without any live credential actions", () => {
    renderInspector(null);
    expect(screen.getByText(/save the workflow to create this endpoint/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /show key|regenerate|rename/i })).not.toBeInTheDocument();
    expect(screen.queryByText("/api/ext/prod-alerts/run")).not.toBeInTheDocument();
  });

  it("reports management errors without showing a key", async () => {
    REQUESTS.mockResolvedValue(new Response(JSON.stringify({ code: "forbidden", message: "Permission denied" }), { status: 403 }));
    renderInspector();
    fireEvent.click(screen.getByRole("button", { name: /show key/i }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Permission denied");
    expect(screen.queryByDisplayValue(/^ext_/)).not.toBeInTheDocument();
  });

  it("uses the existing field modal to compile an input JSON Schema", async () => {
    renderInspector();
    fireEvent.click(screen.getByTitle("Add field"));
    fireEvent.change(screen.getByPlaceholderText("e.g. start_time"), { target: { value: "message" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
    await waitFor(() => expect(CONFIG_CHANGE).toHaveBeenCalledWith(expect.objectContaining({ inputSchema: { type: "object", properties: { message: { type: "string" } }, required: ["message"] } })));
  });

  it("preserves schema constraints and can clear the display name while editing", async () => {
    const client = new QueryClient();
    const config = { slug: "prod-alerts", inputSchema: { type: "object", additionalProperties: false, properties: { message: { type: "string", title: "Alert message", pattern: "^[a-z]+$", description: "Alert text" } }, required: ["message"] } };
    render(<QueryClientProvider client={client}><ExternalTriggerInspector config={config} nodeId="ext" currentSlug="prod-alerts" onConfigChange={CONFIG_CHANGE} /></QueryClientProvider>);
    fireEvent.click(screen.getByTitle("Edit field"));
    fireEvent.change(screen.getByPlaceholderText("message"), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
    await waitFor(() => expect(CONFIG_CHANGE).toHaveBeenCalledWith({ slug: "prod-alerts", inputSchema: { type: "object", additionalProperties: false, properties: { message: { type: "string", pattern: "^[a-z]+$", description: "Alert text" } }, required: ["message"] } }));
  });
});

describe("ExternalTriggerEditor live metadata", () => {
  it("reads the live slug by nodeId, refreshes after rename, and never uses the draft as live identity", async () => {
    let liveSlug = "prod-alerts";
    REQUESTS.mockImplementation(async (url) => {
      if (String(url).endsWith("/rename")) { liveSlug = "draft"; return new Response(null, { status: 204 }); }
      return new Response(JSON.stringify({ triggers: [{ ...SUMMARY, nodeId: "other", slug: "other" }, { ...SUMMARY, slug: liveSlug }] }), { status: 200 });
    });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><ExternalTriggerEditor workflowId="workflow-1" nodeId="ext" config={{ slug: "draft" }} onConfigChange={CONFIG_CHANGE} /></QueryClientProvider>);
    expect(await screen.findByText("/api/ext/prod-alerts/run")).toBeInTheDocument();
    expect(REQUESTS).toHaveBeenCalledWith(expect.stringContaining("/api/workflows/external-triggers?workflowId=workflow-1"), expect.anything());
    fireEvent.click(screen.getByRole("button", { name: /^rename/i }));
    fireEvent.click(within(screen.getByRole("group")).getByRole("button", { name: /confirm/i }));
    expect(await screen.findByText("/api/ext/draft/run")).toBeInTheDocument();
  });

  it("treats an empty list as unsaved, not as a draft-backed endpoint", async () => {
    REQUESTS.mockResolvedValue(new Response(JSON.stringify({ triggers: [] }), { status: 200 }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><ExternalTriggerEditor workflowId="workflow-1" nodeId="ext" config={{ slug: "draft" }} onConfigChange={CONFIG_CHANGE} /></QueryClientProvider>);
    expect(await screen.findByText(/save the workflow to create this endpoint/i)).toBeInTheDocument();
  });

  it("does not mislabel a list error as an unsaved endpoint and permits retry", async () => {
    REQUESTS.mockResolvedValueOnce(new Response(JSON.stringify({ message: "Workflow not found" }), { status: 404 }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><ExternalTriggerEditor workflowId="workflow-1" nodeId="ext" config={{ slug: "draft" }} onConfigChange={CONFIG_CHANGE} /></QueryClientProvider>);
    expect(await screen.findByRole("alert")).toHaveTextContent("Workflow not found");
    expect(screen.queryByText(/save the workflow/i)).not.toBeInTheDocument();
    REQUESTS.mockResolvedValue(new Response(JSON.stringify({ triggers: [SUMMARY] }), { status: 200 }));
    fireEvent.click(screen.getByRole("button", { name: /retry/i }));
    expect(await screen.findByText("/api/ext/prod-alerts/run")).toBeInTheDocument();
  });

  it("refreshes live metadata after saving a newly added node", async () => {
    let saved = false;
    REQUESTS.mockImplementation(async (_url, init) => {
      if (init?.method === "PUT") { saved = true; return new Response(null, { status: 204 }); }
      return new Response(JSON.stringify({ triggers: saved ? [SUMMARY] : [] }), { status: 200 });
    });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { result } = renderHook(() => ({ list: useWorkflowExternalTriggers("workflow-1"), update: useUpdateWorkflow() }), {
      wrapper: ({ children }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
    });
    await waitFor(() => expect(result.current.list.data).toEqual({ triggers: [] }));
    await act(async () => result.current.update.mutateAsync({ workflowId: "workflow-1", input: { name: "Workflow", definition: { schemaVersion: 1, nodes: [], edges: [] }, activations: [] } }));
    await waitFor(() => expect(result.current.list.data).toEqual({ triggers: [SUMMARY] }));
  });
});
