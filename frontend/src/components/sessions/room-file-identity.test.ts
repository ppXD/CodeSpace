import { describe, expect, it } from "vitest";

import { roomFileUrl } from "@/api/sessions";
import { attachmentFileIdentity, roomFileQueryKey, statItemFileIdentity } from "./SessionRoomView";

const exact = {
  path: "README.md",
  agentRunId: "agent-1",
  repositoryId: "repo-api",
  repositoryAlias: "api",
};

describe("room file identity", () => {
  it("binds repository identity into both the query key and URL", () => {
    expect(roomFileQueryKey("run-1", exact)).toEqual(["roomFile", "run-1", "README.md", "agent-1", "repo-api", "api"]);
    expect(roomFileUrl("run-1", exact)).toBe("/api/sessions/by-run/run-1/room/file?path=README.md&agentRunId=agent-1&repositoryId=repo-api&repositoryAlias=api");
  });

  it("uses backend-authored stat and result identities while retaining legacy path-only rows", () => {
    expect(statItemFileIdentity({ text: "README.md", file: exact })).toEqual(exact);
    expect(attachmentFileIdentity({ kind: "FileLink", label: "README.md", file: exact })).toEqual(exact);

    expect(statItemFileIdentity({ text: "legacy.md" })).toEqual({ path: "legacy.md" });
    expect(attachmentFileIdentity({ kind: "FileLink", label: "legacy.md", agentRunId: "agent-old" })).toEqual({ path: "legacy.md", agentRunId: "agent-old" });
  });
});
