import { describe, expect, it } from "vitest";

import { statusWord } from "./runStatus";

describe("statusWord", () => {
  it("maps every run status to one friendly word — the enum never reaches a user", () => {
    expect(statusWord("Success")).toBe("Done");
    expect(statusWord("Failure")).toBe("Failed");
    expect(statusWord("Cancelled")).toBe("Stopped");
    expect(statusWord("Suspended")).toBe("Waiting");
    expect(statusWord("Running")).toBe("Working");
    expect(statusWord("Pending")).toBe("Queued");
    expect(statusWord("Enqueued")).toBe("Queued");
  });

  it("returns an unknown future status verbatim rather than blank", () => {
    expect(statusWord("SomethingNew" as never)).toBe("SomethingNew");
  });
});
