import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { StorageProviderModuleSummary } from "@/api/storage";

import { AddDestinationDialog } from "./AddDestinationDialog";

const provider: StorageProviderModuleSummary = {
  typeKey: "aliyun-oss/v1",
  displayName: "Aliyun OSS",
  configSchema: {
    type: "object",
    properties: {
      endpoint: { type: "string", title: "Endpoint" },
      bucket: { type: "string", title: "Bucket name" },
    },
    required: ["endpoint", "bucket"],
  },
  secretSchema: {
    type: "object",
    properties: {
      accessKeyId: { type: "string", title: "AccessKey ID" },
      accessKeySecret: { type: "string", title: "AccessKey secret", writeOnly: true },
    },
    required: ["accessKeyId", "accessKeySecret"],
  },
  capabilities: ["StreamingWrite"],
  teamNamespaceProperty: "bucket",
};

interface Calls {
  probe: unknown[];
  destination: unknown[];
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function renderDialog(options: { probe?: unknown; dataClasses?: unknown[] } = {}) {
  const calls: Calls = { probe: [], destination: [] };
  localStorage.setItem("codespace.jwt", "test-jwt");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const path = new URL(typeof input === "string" ? input : input.toString(), "http://test.local").pathname;
    if (path === "/api/storage/data-classes") {
      return json(options.dataClasses ?? [
        { typeKey: "workflow-artifact/v1", displayName: "Workflow artifacts", hasLocalFallback: true },
        { typeKey: "agent-run-log/v1", displayName: "Agent run logs", hasLocalFallback: false },
      ]);
    }
    if (path === "/api/storage/probes") {
      calls.probe.push(JSON.parse(String(init.body)));
      return json(options.probe ?? { providerTypeKey: provider.typeKey, status: "Available", latencyMilliseconds: 214, failure: null });
    }
    if (path === "/api/storage/destinations") {
      calls.destination.push(JSON.parse(String(init.body)));
      return json({ profileId: "p1", name: "codespace-artifacts", providerTypeKey: provider.typeKey, profileRevision: 1, state: "Active", credentialId: "c1", credentialRevision: 1, dataClassTypeKeys: [] });
    }
    return json([]);
  }));

  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });
  const view = render(
    <QueryClientProvider client={client}>
      <AddDestinationDialog providers={[provider]} onClose={() => {}} onCreated={() => {}} />
    </QueryClientProvider>,
  );
  return { calls, view };
}

/**
 * SchemaForm renders a string field as a contenteditable picker rather than an input, so a field is reached through
 * its own row rather than by label association. Secret fields, which SchemaForm renders as password inputs carrying
 * an aria-label, are reached directly.
 */
function setConfigField(label: string, value: string) {
  const row = Array.from(document.querySelectorAll<HTMLElement>(".wf-form-row"))
    .find((candidate) => candidate.querySelector(".wf-form-label")?.textContent?.startsWith(label));
  if (!row) throw new Error(`No form row labelled ${label}`);
  const editor = row.querySelector<HTMLElement>('[role="textbox"]');
  if (!editor) throw new Error(`Row ${label} has no editor`);
  editor.innerHTML = value;
  fireEvent.input(editor);
}

function fill() {
  setConfigField("Endpoint", "oss-cn-hongkong.aliyuncs.com");
  setConfigField("Bucket name", "codespace-artifacts");
  fireEvent.change(screen.getByLabelText("AccessKey ID"), { target: { value: "LTAI5tExample" } });
  fireEvent.change(screen.getByLabelText("AccessKey secret"), { target: { value: "secret-value" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("AddDestinationDialog", () => {
  // The whole reason this dialog exists. A storage profile cannot be deleted, so if the test that finds out whether a
  // key works ran after the key was recorded, a typo would cost a permanent row. Testing first is not a nicety.
  it("tests the real destination without recording anything", async () => {
    const { calls } = renderDialog();
    fill();

    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));

    await screen.findByRole("button", { name: "Start storing here" });
    expect(calls.probe).toHaveLength(1);
    expect(calls.destination).toHaveLength(0);
  });

  // A refused test has to say three things: what to do about it, what the machine called it, and that it cost nothing.
  // The third is what turns "I got it wrong" from a cleanup problem into a retry.
  it("names a wrong secret as a signature problem and says nothing was saved", async () => {
    const { calls } = renderDialog({ probe: { providerTypeKey: provider.typeKey, status: "Unavailable", latencyMilliseconds: 180, failure: { stage: "Probe", code: "ProbeSignatureMismatch", retryable: false } } });
    fill();

    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));

    expect(await screen.findByText(/rejected the request signature/i)).toBeInTheDocument();
    expect(screen.getByText(/Probe \/ ProbeSignatureMismatch/)).toBeInTheDocument();
    expect(screen.getByText(/Nothing was saved/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Test again" })).toBeInTheDocument();
    expect(calls.destination).toHaveLength(0);
  });

  // Ticking a box is the one irreversible-ish choice in the dialog, so nothing is ticked for the operator.
  it("ticks nothing for the operator and sends only what they ticked", async () => {
    const { calls } = renderDialog();
    fill();
    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));
    await screen.findByRole("button", { name: "Start storing here" });

    const boxes = screen.getAllByRole("checkbox");
    expect(boxes.every((box) => !(box as HTMLInputElement).checked)).toBe(true);

    fireEvent.click(boxes[0]);
    fireEvent.click(screen.getByRole("button", { name: "Start storing here" }));

    await waitFor(() => expect(calls.destination).toHaveLength(1));
    expect(calls.destination[0]).toMatchObject({ dataClassTypeKeys: ["workflow-artifact/v1"], name: "codespace-artifacts" });
  });

  // The two sentences differ because the facts differ: one class already has a home, the other is being dropped on
  // the floor. Read off the class's own declaration, so a future data class needs no change here.
  it("says what happens to an unticked class, differently for a class with no home of its own", async () => {
    renderDialog();
    fill();
    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));
    await screen.findByRole("button", { name: "Start storing here" });

    expect(screen.getByText(/written to this server's own disk/i)).toBeInTheDocument();
    expect(screen.getByText(/Not captured at all today/i)).toBeInTheDocument();
  });

  // Four typed fields, not five: the name is the bucket unless the operator says otherwise.
  it("derives the name from the provider's own namespace field and lets it be overruled", async () => {
    const { calls } = renderDialog();
    fill();

    expect(screen.getByLabelText("Name")).toHaveValue("codespace-artifacts");

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "cold-storage" } });
    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));
    await screen.findByRole("button", { name: "Start storing here" });
    fireEvent.click(screen.getByRole("button", { name: "Start storing here" }));

    await waitFor(() => expect(calls.destination).toHaveLength(1));
    expect(calls.destination[0]).toMatchObject({ name: "cold-storage" });
  });

  // The secret must never be echoed into the non-secret hint. Only a field the schema does NOT mark writeOnly can be.
  it("builds the key hint from the AccessKey ID and never from the secret", async () => {
    const { calls } = renderDialog();
    fill();
    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));
    await screen.findByRole("button", { name: "Start storing here" });
    fireEvent.click(screen.getByRole("button", { name: "Start storing here" }));

    await waitFor(() => expect(calls.destination).toHaveLength(1));
    const sent = calls.destination[0] as { safeHint: string };
    expect(sent.safeHint).toBe("LTAI5tE…mple");
    expect(sent.safeHint).not.toContain("secret-value");
  });

  it("will not test until every required field the provider declares is filled", () => {
    renderDialog();

    expect(screen.getByRole("button", { name: "Test connection" })).toBeDisabled();

    fill();

    expect(screen.getByRole("button", { name: "Test connection" })).toBeEnabled();
  });
});
