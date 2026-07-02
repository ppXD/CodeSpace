import { describe, expect, it } from "vitest";

import { emptyEvidenceLabel, formatCost, formatDuration, formatSuccessRate, initials, outcomeTone, successTone } from "./agentEvidence";

describe("agentEvidence formatters", () => {
  it("formats the success rate as a whole percent", () => {
    expect(formatSuccessRate(0)).toBe("0%");
    expect(formatSuccessRate(0.7)).toBe("70%");
    expect(formatSuccessRate(2 / 3)).toBe("67%");
    expect(formatSuccessRate(1)).toBe("100%");
  });

  it("formats durations compactly and em-dashes a missing one", () => {
    expect(formatDuration(null)).toBe("—");
    expect(formatDuration(8)).toBe("8s");
    expect(formatDuration(90)).toBe("1m 30s");
    expect(formatDuration(120)).toBe("2m");
    expect(formatDuration(7500)).toBe("2h 5m");
    expect(formatDuration(7200)).toBe("2h");
  });

  it("rounds fractional seconds at boundaries without emitting 60s / 1m 60s", () => {
    expect(formatDuration(59.6)).toBe("1m");
    expect(formatDuration(119.6)).toBe("2m");
    expect(formatDuration(3599.6)).toBe("1h");
    expect(formatDuration(0.4)).toBe("0s");
  });

  it("formats cost, keeping em-dash distinct from a real $0", () => {
    expect(formatCost(null)).toBe("—");
    expect(formatCost(0)).toBe("$0.00");
    expect(formatCost(1.94)).toBe("$1.94");
  });

  it("maps an outcome to a sparkline tone", () => {
    expect(outcomeTone("Succeeded")).toBe("good");
    expect(outcomeTone("Failed")).toBe("danger");
    expect(outcomeTone("TimedOut")).toBe("danger");
    expect(outcomeTone("Cancelled")).toBe("danger");
    expect(outcomeTone("NeedsReview")).toBe("danger");
    expect(outcomeTone("Running")).toBe("muted");
    expect(outcomeTone("Queued")).toBe("muted");
  });

  it("derives avatar initials from one or two words", () => {
    expect(initials("Bug Report")).toBe("BR");
    expect(initials("backend")).toBe("BA");
    expect(initials("  frontend architect reviewer ")).toBe("FA");
    expect(initials("")).toBe("?");
  });

  it("mutes the success tone when nothing is scored yet, not red 0%", () => {
    expect(successTone(0, 0)).toBe("muted");
    expect(successTone(0.9, 10)).toBe("good");
    expect(successTone(0.5, 10)).toBe("warn");
    expect(successTone(0.22, 9)).toBe("danger");
  });

  it("distinguishes the four empty-evidence causes, only calling it 'No runs yet' when truly never-ran", () => {
    expect(emptyEvidenceLabel({ pending: true, error: false, windowed: true })).toBe("Loading recent runs…");
    expect(emptyEvidenceLabel({ pending: false, error: true, windowed: false })).toBe("Run stats unavailable");
    expect(emptyEvidenceLabel({ pending: false, error: false, windowed: true })).toBe("No runs in this window");
    expect(emptyEvidenceLabel({ pending: false, error: false, windowed: false })).toContain("No runs yet");
  });
});
