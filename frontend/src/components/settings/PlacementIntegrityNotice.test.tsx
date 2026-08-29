import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { PlacementIntegritySummary } from "@/api/storage";

import { PlacementIntegrityNotice } from "./PlacementIntegrityNotice";

const stored = (overrides: Partial<PlacementIntegritySummary> = {}): PlacementIntegritySummary => ({
  missing: 0,
  corrupt: 0,
  available: 12,
  oldestVerifiedAt: new Date(Date.now() - 3 * 86_400_000).toISOString(),
  ...overrides,
});

describe("PlacementIntegrityNotice", () => {
  it("says nothing at all before the team has stored anything", () => {
    // An empty team has no loss and no reassurance to give; a green "all present" over zero objects is a claim
    // about nothing, and it trains the reader to skim past the line that will one day matter.
    const { container } = render(<PlacementIntegrityNotice integrity={stored({ available: 0 })} />);

    expect(container).toBeEmptyDOMElement();
  });

  it("renders nothing while the answer has not arrived, rather than guessing", () => {
    const { container } = render(<PlacementIntegrityNotice integrity={undefined} />);

    expect(container).toBeEmptyDOMElement();
  });

  it("names losses apart, because gone and replaced are different problems", () => {
    render(<PlacementIntegrityNotice integrity={stored({ missing: 2, corrupt: 1 })} />);

    expect(screen.getByText(/2 no longer at their destination/)).toBeInTheDocument();
    expect(screen.getByText(/1 replaced by something else/)).toBeInTheDocument();
  });

  it("counts losses against the whole population so the scale is readable", () => {
    render(<PlacementIntegrityNotice integrity={stored({ available: 4000, missing: 2 })} />);

    expect(screen.getByText(/out of 4,002 stored/)).toBeInTheDocument();
  });

  it("qualifies a clean report with how far back the checking actually reaches", () => {
    // "All present" on its own would be read as "checked just now". The oldest confirmation is the honest edge of
    // what is known, and it is the number that tells an operator whether to trust the green.
    render(<PlacementIntegrityNotice integrity={stored()} />);

    expect(screen.getByText(/all present/)).toBeInTheDocument();
    expect(screen.getByText(/least recently confirmed was checked 3 days ago/)).toBeInTheDocument();
  });
});
