import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { StorageSettings } from "./StorageSettings";

describe("storage provider catalog", () => {
  function renderSettings(response: Response) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", "team-1");
    vi.stubGlobal("fetch", vi.fn(async () => response));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });

    return render(<QueryClientProvider client={client}><StorageSettings /></QueryClientProvider>);
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("shows installed provider types and their declared capabilities without exposing mutations", async () => {
    renderSettings(new Response(JSON.stringify([
      {
        typeKey: "aliyun-oss/v1",
        displayName: "Aliyun OSS",
        configSchema: { type: "object", properties: { bucket: { type: "string" } } },
        secretSchema: { type: "object", properties: { accessKeySecret: { type: "string" } } },
        capabilities: ["ConditionalCreate", "MultipartUpload", "StreamingRead", "StreamingWrite"],
      },
      {
        typeKey: "local-rwx/v1",
        displayName: "Local / shared filesystem",
        configSchema: { type: "object", properties: { rootPath: { type: "string" } } },
        secretSchema: { type: "object", properties: {} },
        capabilities: ["ConditionalCreate"],
      },
    ]), { status: 200, headers: { "Content-Type": "application/json" } }));

    expect(screen.getByRole("heading", { name: "Artifact storage" })).toBeTruthy();
    expect(screen.getByText(/Existing workflow runs continue to use the deployment-managed artifact store/)).toBeTruthy();

    await waitFor(() => expect(screen.getByText("Aliyun OSS")).toBeTruthy());

    expect(screen.getByText("aliyun-oss/v1")).toBeTruthy();
    expect(screen.getByText("Local / shared filesystem")).toBeTruthy();
    expect(screen.getByText(/Multipart upload/)).toBeTruthy();
    expect(screen.getAllByText("Profile schema ready")).toHaveLength(2);
    expect(screen.getByText("No secret inputs")).toBeTruthy();
    expect(screen.queryByRole("button")).toBeNull();
    expect(screen.queryByRole("textbox")).toBeNull();

    expect(globalThis.fetch).toHaveBeenCalledWith(expect.stringContaining("/api/storage/provider-modules"), expect.objectContaining({
      headers: expect.any(Headers),
    }));
    const request = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0][1] as RequestInit;
    expect((request.headers as Headers).get("X-Team-Id")).toBe("team-1");
  });

  it("keeps deployment storage authoritative when this build has no provider modules", async () => {
    renderSettings(new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } }));

    await waitFor(() => expect(screen.getByText("No storage provider modules installed")).toBeTruthy());

    expect(screen.getByText(/does not change where current run artifacts, model calls, or logs are written/)).toBeTruthy();
  });

  it("reports catalog failures without presenting an empty catalog as truth", async () => {
    renderSettings(new Response(JSON.stringify({ code: "catalog_unavailable", message: "Catalog unavailable" }), {
      status: 503,
      statusText: "Service Unavailable",
      headers: { "Content-Type": "application/json" },
    }));

    await waitFor(() => expect(screen.getByText("Couldn't load storage providers")).toBeTruthy());

    expect(screen.getByText("Catalog unavailable")).toBeTruthy();
    expect(screen.queryByText("No storage provider modules installed")).toBeNull();
  });
});
