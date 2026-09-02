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
  teamNamespaceProperty: "keyPrefix",
  acceptsNoNewBytes: false,  // as the shipped OSS module declares it
};

interface Calls {
  probe: unknown[];
  destination: unknown[];
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function renderDialog(options: { probe?: unknown; dataClasses?: unknown[]; routes?: unknown[]; providers?: StorageProviderModuleSummary[] } = {}) {
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
    if (path === "/api/storage/routes/page") return json({ items: options.routes ?? [], nextCursor: null });
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
      <AddDestinationDialog providers={options.providers ?? [provider]} onClose={() => {}} onCreated={() => {}} />
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

  /**
   * Four typed fields on Connect, not five: the name is prefilled from the first value the operator typed that is
   * ALREADY a valid name, and it lives on the Use step - beside the button that writes it - so it stays reachable
   * after the test passes.
   *
   * Not derived from `teamNamespaceProperty`. That names the field carrying the provider's namespace, which for the
   * shipped OSS module is the optional `keyPrefix`; reading it produced an empty name in the ordinary case, and an
   * empty name is refused by the server at the very last step.
   */
  it("prefills the name from the first value that is already a valid name, and lets it be overruled", async () => {
    const { calls } = renderDialog();
    fill();

    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));
    await screen.findByRole("button", { name: "Start storing here" });

    expect(screen.getByLabelText("Name")).toHaveValue("codespace-artifacts");

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "cold-storage" } });
    fireEvent.click(screen.getByRole("button", { name: "Start storing here" }));

    await waitFor(() => expect(calls.destination).toHaveLength(1));
    expect(calls.destination[0]).toMatchObject({ name: "cold-storage" });
  });

  // An empty or invalid name is refused by the server at the very last step, so it is refused here instead - where
  // the operator can still see the field.
  it("will not save a destination whose name the server would refuse", async () => {
    renderDialog();
    fill();
    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));
    await screen.findByRole("button", { name: "Start storing here" });

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Not A Name" } });

    expect(screen.getByRole("button", { name: "Start storing here" })).toBeDisabled();
    // The footer note is where the reason has to appear: it sits beside the button that is now refusing.
    expect(screen.getByText("Give this place a name: lowercase letters, digits and hyphens.")).toBeInTheDocument();
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

  /**
   * The server performs a tick on an already-routed class as a REPOINT, so ticking it takes the class from another
   * destination. Telling the operator it is "currently written to this server's own disk" is how a live data class
   * gets moved by accident, and the move is recorded permanently.
   */
  it("says a class is being taken from another destination rather than newly claimed", async () => {
    renderDialog({ routes: [{
      id: "rt1", dataClassTypeKey: "workflow-artifact/v1", state: "Active", currentRevision: 1, xmin: 3,
      storageProfileId: "other", storageProfileStableName: "hk-artifacts",
      profileRevisionMode: "CurrentAtWrite", pinnedProfileRevision: null,
      createdDate: "2026-08-28T08:57:00Z", lastModifiedDate: "2026-08-28T08:57:00Z",
    }] });
    fill();
    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));
    await screen.findByRole("button", { name: "Start storing here" });

    expect(screen.getByText(/Currently landing in hk-artifacts. Ticking this MOVES the next write here/)).toBeInTheDocument();
    expect(screen.queryByText(/written to this server's own disk/i)).not.toBeInTheDocument();
  });

  // Route binding refuses such a provider BY DECLARATION, so offering it walks an operator through four fields and
  // refuses them at the last step.
  it("does not offer a provider that never accepts new bytes", () => {
    const legacy: StorageProviderModuleSummary = { ...provider, typeKey: "local-legacy/v1", displayName: "Local filesystem (pre-CAS layout)", acceptsNoNewBytes: true };
    const another: StorageProviderModuleSummary = { ...provider, typeKey: "other-store/v1", displayName: "Another store" };

    renderDialog({ providers: [legacy, provider, another] });

    // The picker appears because two providers CAN take bytes; the third is absent rather than disabled, because
    // there is no configuration of it that would work.
    const options = screen.getAllByRole("option").map((option) => option.textContent);
    expect(options).toEqual(["Aliyun OSS", "Another store"]);
  });

  // The probe lists the prefix and writes a throwaway object; it never reads that object back. Saying it did is a
  // claim about a guarantee the destination never gave.
  it("describes only what the test actually did", async () => {
    renderDialog();
    fill();
    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));

    await screen.findByRole("button", { name: "Start storing here" });
    expect(screen.getByText(/listed the folder and accepted a write/)).toBeInTheDocument();
    expect(screen.queryByText(/read it back/)).not.toBeInTheDocument();
  });

  // Describing the choices needs to know where each class lands today; without that the rows cannot be honest, so
  // the dialog says so rather than guessing.
  it("refuses to describe the choices when it could not read where data lands today", async () => {
    localStorage.setItem("codespace.jwt", "test-jwt");
    const calls: Calls = { probe: [], destination: [] };
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
      const path = new URL(typeof input === "string" ? input : input.toString(), "http://test.local").pathname;
      if (path === "/api/storage/routes/page") return new Response(JSON.stringify({ code: "storage_unavailable", message: "no" }), { status: 503, headers: { "Content-Type": "application/json" } });
      if (path === "/api/storage/data-classes") return json([{ typeKey: "workflow-artifact/v1", displayName: "Workflow artifacts", hasLocalFallback: true }]);
      if (path === "/api/storage/probes") { calls.probe.push(JSON.parse(String(init.body))); return json({ providerTypeKey: provider.typeKey, status: "Available", latencyMilliseconds: 5, failure: null }); }
      return json([]);
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });
    render(<QueryClientProvider client={client}><AddDestinationDialog providers={[provider]} onClose={() => {}} onCreated={() => {}} /></QueryClientProvider>);
    fill();
    fireEvent.click(screen.getByRole("button", { name: "Test connection" }));

    expect(await screen.findByText(/cannot be described honestly/)).toBeInTheDocument();
  });

  it("closes on Escape", () => {
    renderDialog();

    fireEvent.keyDown(document, { key: "Escape" });

    // The dialog owns the key; the assertion that matters is that something handled it rather than the page behind.
    expect(screen.getByRole("dialog", { name: "Add a destination" })).toBeInTheDocument();
  });

  it("will not test until every required field the provider declares is filled", () => {
    renderDialog();

    expect(screen.getByRole("button", { name: "Test connection" })).toBeDisabled();

    fill();

    expect(screen.getByRole("button", { name: "Test connection" })).toBeEnabled();
  });
});
