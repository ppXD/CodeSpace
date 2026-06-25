import { describe, expect, it } from "vitest";

import { sourceLabel } from "./runsIndex";

describe("sourceLabel", () => {
  it("title-cases a source token and falls back for an empty one", () => {
    expect(sourceLabel("manual")).toBe("Manual");
    expect(sourceLabel("schedule.cron")).toBe("Schedule.cron");
    expect(sourceLabel("")).toBe("Run");
  });
});
