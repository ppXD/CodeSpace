import { describe, expect, it } from "vitest";

import { deriveRole } from "./agentRole";

const a = (name: string, description: string | null = null) => ({ name, description });

describe("deriveRole", () => {
  it("classifies a reviewer from security/review wording", () => {
    expect(deriveRole(a("Security reviewer", "Audits diffs for vulnerabilities"))).toBe("Reviewer");
    expect(deriveRole(a("QA", "Reviews pull requests"))).toBe("Reviewer");
  });

  it("classifies a tracer from bug/triage wording", () => {
    expect(deriveRole(a("Bug report", "Triages and reproduces reported bugs"))).toBe("Tracer");
    expect(deriveRole(a("Incident debugger"))).toBe("Tracer");
  });

  it("classifies a planner from coordination wording", () => {
    expect(deriveRole(a("Sprint planner", "Plans and coordinates the roadmap"))).toBe("Planner");
    expect(deriveRole(a("Supervisor"))).toBe("Planner");
  });

  it("classifies an architect from design/backend wording", () => {
    expect(deriveRole(a("Backend architect", "Designs and implements backend services and APIs"))).toBe("Architect");
    expect(deriveRole(a("Frontend engineer"))).toBe("Architect");
  });

  it("falls back to Generalist when nothing matches", () => {
    expect(deriveRole(a("Helper", "Does assorted tasks"))).toBe("Generalist");
    expect(deriveRole(a("Agent", null))).toBe("Generalist");
  });

  it("prefers the more specific role when several words appear", () => {
    expect(deriveRole(a("Security architect", "Designs and reviews secure backend systems"))).toBe("Reviewer");
    expect(deriveRole(a("Bug-fixing backend dev", "Reproduces and fixes backend bugs"))).toBe("Tracer");
  });
});
