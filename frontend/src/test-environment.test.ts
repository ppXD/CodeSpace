import { describe, expect, it } from "vitest";

/// Unit: the guarantees vitest.setup.ts makes about the test environment itself.
///
/// This exists because those guarantees have already failed silently once. `localStorage` disappeared from the
/// environment under a dependency bump, and because request.ts / client.ts read it directly and the setup's `beforeEach`
/// clears it, the symptom was all 1728 specs failing at once — a signal that reads as "the suite is broken" and says
/// nothing about the cause. These assertions fail with the cause named instead.
describe("test environment", () => {
  it.each(["localStorage", "sessionStorage"] as const)("provides a working %s", (name) => {
    const storage = (globalThis as unknown as Record<string, Storage>)[name];

    expect(storage, `${name} is missing — specs that read it fail before their own body runs`).toBeDefined();

    storage.setItem("k", "v");
    expect(storage.getItem("k")).toBe("v");
    expect(storage.length).toBe(1);
    expect(storage.key(0)).toBe("k");

    storage.removeItem("k");
    expect(storage.getItem("k")).toBeNull();
    expect(storage.length).toBe(0);
  });

  it("hands each test an empty localStorage", () => {
    // The pair below prove the beforeEach clear actually runs: whichever executes second would see the other's key.
    expect(localStorage.length).toBe(0);
    localStorage.setItem("bleed", "1");
  });

  it("hands each test an empty localStorage, again", () => {
    expect(localStorage.getItem("bleed"), "a key set by another test survived — cross-test state can silently green a regression").toBeNull();
    localStorage.setItem("bleed", "2");
  });
});
