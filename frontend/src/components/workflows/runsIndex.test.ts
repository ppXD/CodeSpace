import { describe, expect, it } from "vitest";

import { runKindLabel, sourceLabel } from "./runsIndex";

describe("runKindLabel", () => {
  it("labels each run kind in plain language", () => {
    expect(runKindLabel("workflow")).toBe("Automation");
    expect(runKindLabel("task")).toBe("Task");
    expect(runKindLabel("event")).toBe("Triggered");
    expect(runKindLabel("replay")).toBe("Re-run");
    expect(runKindLabel("schedule")).toBe("Scheduled");
    expect(runKindLabel("child")).toBe("Sub-workflow");
  });

  it("title-cases an unknown/future kind and never renders blank", () => {
    expect(runKindLabel("mystery")).toBe("Mystery");
    expect(runKindLabel("")).toBe("Run");
  });
});

describe("sourceLabel", () => {
  it("maps the common launch sources to plain language", () => {
    expect(sourceLabel("manual")).toBe("Launched by you");
    expect(sourceLabel("schedule.cron")).toBe("Scheduled");
    expect(sourceLabel("workflow.child")).toBe("Sub-workflow");
    expect(sourceLabel("api")).toBe("API");
  });

  it("names the provider for a provider trigger", () => {
    expect(sourceLabel("provider.github.pull_request")).toBe("From GitHub");
    expect(sourceLabel("provider.gitlab.push")).toBe("From GitLab");
    expect(sourceLabel("provider.custom.event")).toBe("From Custom");   // unknown provider → title-case
  });

  it("title-cases an unrecognised source and falls back for an empty one", () => {
    expect(sourceLabel("webhook")).toBe("Webhook");
    expect(sourceLabel("")).toBe("Run");
  });
});
