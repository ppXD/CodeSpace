import { describe, expect, it } from "vitest";
import type { TaskAcceptanceCompatibility, TaskSpecSuggestion } from "@/api/tasks";
import { adoptableSpecChecks, sourceSupportedSpecChecks } from "./specProposal";

const supported = (argv = ["custom-check", "--assert"]): TaskSpecSuggestion => ({
  acceptanceChecks: argv, acceptanceCriteria: [], rationale: "", confidence: 0.9,
  acceptanceProposal: { version: 1, argv, source: "user-explicit", status: "Supported", reason: "Reviewed in the original goal", commandDigest: "command-hash", sourceDigest: "source-hash", dependencies: [], evidence: [{ sourceId: "goal", kind: "user-goal", contentDigest: "source-hash", quote: "Run custom-check --assert" }] },
});
const compatible: TaskAcceptanceCompatibility = { state: "Compatible", projectionKind: "agent.single", detail: "TestsPass adapter" };

describe("spec adoption separates evidence from execution compatibility", () => {
  it("keeps exact supported argv including empty and whitespace arguments", () => {
    const suggestion = supported(["custom-check", "", " ", "--assert"]);
    expect(sourceSupportedSpecChecks(suggestion)).toEqual(suggestion.acceptanceChecks);
    expect(adoptableSpecChecks(suggestion, compatible)).toEqual(suggestion.acceptanceChecks);
  });
  it.each(["", " ", "\t"])("rejects a blank executable without promoting an argument into the command", executable => {
    expect(adoptableSpecChecks(supported([executable, "custom-check"]), compatible)).toEqual([]);
  });
  it.each([undefined, { ...compatible, state: "Unknown" as const }, { ...compatible, state: "Incompatible" as const }])("keeps unsupported execution as a proposal", capability => {
    expect(sourceSupportedSpecChecks(supported())).toEqual(["custom-check", "--assert"]);
    expect(adoptableSpecChecks(supported(), capability)).toEqual([]);
  });
  it.each([undefined, "Unknown", "Contradicted"])("cannot adopt absent or %s source evidence", status => {
    const suggestion = supported();
    suggestion.acceptanceProposal = status ? { ...suggestion.acceptanceProposal!, status: status as "Unknown" | "Contradicted" } : undefined;
    expect(adoptableSpecChecks(suggestion, compatible)).toEqual([]);
  });
  it("rejects stale command metadata even when the source review was supported", () => {
    const suggestion = supported();
    suggestion.acceptanceChecks = ["different-command"];
    expect(adoptableSpecChecks(suggestion, compatible)).toEqual([]);
  });
});
