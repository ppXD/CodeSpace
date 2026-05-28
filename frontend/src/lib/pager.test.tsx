import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { Pager } from "@/_imported/ai-code-space/pager";

/**
 * Tests for the shared <code>Pager</code>. Lives under src/lib/ (not co-located
 * with pager.tsx) because vitest excludes the <code>_imported/**</code>
 * directory from its test sweep. Imports the component via the path alias so
 * the source location can move later without touching the test.
 *
 * <p>Pinned contract: <b>loading state does NOT gate navigation</b>. Operators
 * must be able to queue a page change while data is still being fetched —
 * disabling buttons during loads stranded the picker on page 1 while the
 * eager-fetch loop ran for tens of seconds. Disabled state reflects only
 * structural constraints (current page, current === 1 for Previous,
 * !hasNext for Next).</p>
 */
describe("Pager — loading doesn't gate navigation", () => {
  it("page-number buttons remain clickable while loading", () => {
    const onChange = vi.fn();
    render(<Pager current={1} totalPages={5} hasNext={true} loading={true} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: "Page 2" }));

    expect(onChange).toHaveBeenCalledWith(2);
  });

  it("Next button is clickable while loading when hasNext is true", () => {
    const onChange = vi.fn();
    render(<Pager current={1} totalPages={5} hasNext={true} loading={true} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: "Next page" }));

    expect(onChange).toHaveBeenCalledWith(2);
  });

  it("Previous button is clickable while loading when current > 1", () => {
    const onChange = vi.fn();
    render(<Pager current={3} totalPages={5} hasNext={true} loading={true} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: "Previous page" }));

    expect(onChange).toHaveBeenCalledWith(2);
  });
});

describe("Pager — structural disables stay in place", () => {
  it("Previous is disabled at page 1 even when not loading", () => {
    render(<Pager current={1} totalPages={5} hasNext={true} loading={false} onChange={vi.fn()} />);

    expect(screen.getByRole("button", { name: "Previous page" })).toBeDisabled();
  });

  it("Next is disabled when hasNext is false even when not loading", () => {
    render(<Pager current={5} totalPages={5} hasNext={false} loading={false} onChange={vi.fn()} />);

    expect(screen.getByRole("button", { name: "Next page" })).toBeDisabled();
  });

  it("the current-page button is disabled (clicking it would re-trigger the same page)", () => {
    render(<Pager current={2} totalPages={5} hasNext={true} loading={false} onChange={vi.fn()} />);

    expect(screen.getByRole("button", { name: "Page 2" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Page 3" })).not.toBeDisabled();
  });
});
