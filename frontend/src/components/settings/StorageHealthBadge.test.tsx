import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { StorageProfileHealthSummary } from "@/api/storage";

import { StorageHealthBadge } from "./StorageHealthBadge";

const verified: StorageProfileHealthSummary = {
  status: "Available",
  writeVerified: true,
  profileRevision: 3,
  failureStage: null,
  failureCode: null,
  latencyMilliseconds: 42,
  observedAt: new Date().toISOString(),
};

describe("StorageHealthBadge", () => {
  it("says nobody has checked rather than showing a neutral pass", () => {
    // The state a freshly created profile is in, and the one most likely to be mistaken for working. A badge that
    // rendered it as green would be the settings page telling a comforting lie.
    render(<StorageHealthBadge health={null} currentRevision={1} />);

    expect(screen.getByText("not checked")).toBeInTheDocument();
    expect(screen.queryByText(/verified/i)).not.toBeInTheDocument();
  });

  it("carries the provider's own stage and code for a destination that is not taking bytes", () => {
    // Without them a credential problem and a bucket problem look identical, and an operator cannot tell which
    // end to fix without re-running the probe themselves.
    render(<StorageHealthBadge currentRevision={3} health={{
      ...verified, status: "Unavailable", writeVerified: false, failureStage: "Credential", failureCode: "CredentialMissing",
    }} />);

    expect(screen.getByText("unreachable")).toBeInTheDocument();
    expect(screen.getByTitle(/Credential\/CredentialMissing/)).toBeInTheDocument();
    expect(screen.getByTitle(/will not land/i)).toBeInTheDocument();
  });

  it("words a lifecycle gate as a lifecycle gate, never as a destination that cannot be reached", () => {
    // ProfileNotActive is the profile refusing to admit a WRITE. It is not the destination failing to answer —
    // reads of every object stored there are still admitted — so rendering it as "unreachable" claims a fact
    // nothing measured, which is the exact lie this badge exists to prevent.
    render(<StorageHealthBadge currentRevision={3} health={{
      ...verified, status: "Unavailable", writeVerified: false, failureStage: "Profile", failureCode: "ProfileNotActive",
    }} />);

    expect(screen.getByText("writes disabled")).toBeInTheDocument();
    expect(screen.queryByText("unreachable")).not.toBeInTheDocument();
    expect(screen.queryByTitle(/will not land/i)).not.toBeInTheDocument();
  });

  it("does not read a read-only pass as writes working", () => {
    // Listing proves the credential reaches the bucket. It does not prove a run's bytes will land, and the two
    // must not render the same.
    render(<StorageHealthBadge health={{ ...verified, writeVerified: false }} currentRevision={3} />);

    expect(screen.getByText("reachable, writes unverified")).toBeInTheDocument();
  });

  it("does not present a pass against an older revision as current", () => {
    // Health for revision 3 says nothing about revision 4 — the profile has moved to a destination nothing checked.
    render(<StorageHealthBadge health={verified} currentRevision={4} />);

    expect(screen.getByText("unchecked since it changed")).toBeInTheDocument();
    expect(screen.queryByText("writes verified")).not.toBeInTheDocument();
  });

  it("reports a proven destination as proven", () => {
    render(<StorageHealthBadge health={verified} currentRevision={3} />);

    expect(screen.getByText("writes verified")).toBeInTheDocument();
    expect(screen.getByTitle(/written and removed/i)).toBeInTheDocument();
  });

  it("says a failing status without pretending the reason is known when it is not", () => {
    render(<StorageHealthBadge currentRevision={3} health={{ ...verified, status: "ReadOnly", writeVerified: false, failureStage: null, failureCode: null }} />);

    const badge = screen.getByText("refusing writes");
    expect(badge).toBeInTheDocument();
    expect(screen.getByTitle(/Last checked/)).not.toHaveAttribute("title", expect.stringContaining("undefined"));
  });
});
