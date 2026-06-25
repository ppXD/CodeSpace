import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { Pager } from "./Pager";
import { pagerPages } from "./pagerPages";

describe("pagerPages", () => {
  it("returns just [1] for a single page or none", () => {
    expect(pagerPages(1, 1)).toEqual([1]);
    expect(pagerPages(1, 0)).toEqual([1]);
  });

  it("lists every page with no ellipsis for a small total (<=7)", () => {
    expect(pagerPages(3, 7)).toEqual([1, 2, 3, 4, 5, 6, 7]);
  });

  it("windows around the current page with ellipses for a large total", () => {
    expect(pagerPages(5, 12)).toEqual([1, "ellipsis", 4, 5, 6, "ellipsis", 12]);
  });

  it("drops the leading ellipsis when the window touches the start", () => {
    expect(pagerPages(2, 12)).toEqual([1, 2, 3, "ellipsis", 12]);
  });

  it("drops the trailing ellipsis when the window touches the end", () => {
    expect(pagerPages(11, 12)).toEqual([1, "ellipsis", 10, 11, 12]);
  });

  it("clamps the current page into range", () => {
    expect(pagerPages(99, 12)).toEqual([1, "ellipsis", 11, 12]);   // past-end → last
    expect(pagerPages(-5, 12)).toEqual([1, 2, "ellipsis", 12]);    // below-start → first
  });
});

describe("Pager", () => {
  it("renders nothing when everything fits on one page", () => {
    const { container } = render(<Pager page={1} pageSize={20} total={20} onPage={() => {}} />);
    expect(container.querySelector(".runs-pager")).toBeNull();
  });

  it("accents the current page and disables the arrow at the boundary", () => {
    render(<Pager page={1} pageSize={20} total={60} onPage={() => {}} />);   // 3 pages, on the first

    expect(screen.getByRole("button", { name: "1" }).getAttribute("data-current")).toBe("true");
    expect(screen.getByRole("button", { name: /previous page/i })).toBeDisabled();
    expect(screen.getByRole("button", { name: /next page/i })).not.toBeDisabled();
  });

  it("reports the target page on a number click and on the arrows", () => {
    const onPage = vi.fn();
    render(<Pager page={2} pageSize={20} total={60} onPage={onPage} />);   // 3 pages, on page 2

    fireEvent.click(screen.getByRole("button", { name: "3" }));
    expect(onPage).toHaveBeenCalledWith(3);

    fireEvent.click(screen.getByRole("button", { name: /previous page/i }));
    expect(onPage).toHaveBeenCalledWith(1);

    fireEvent.click(screen.getByRole("button", { name: /next page/i }));
    expect(onPage).toHaveBeenCalledWith(3);
  });

  it("clamps an out-of-range page to the last page — accent, bounds, and navigation all use the clamped value", () => {
    const onPage = vi.fn();
    render(<Pager page={9} pageSize={20} total={60} onPage={onPage} />);   // 3 pages, but the parent is stranded on 9

    expect(screen.getByRole("button", { name: "3" }).getAttribute("data-current")).toBe("true");   // last page accented, not the phantom 9
    expect(screen.getByRole("button", { name: /next page/i })).toBeDisabled();                       // at the clamped end
    expect(screen.getByRole("button", { name: /previous page/i })).not.toBeDisabled();

    fireEvent.click(screen.getByRole("button", { name: /previous page/i }));
    expect(onPage).toHaveBeenCalledWith(2);   // 3 - 1 (from the clamped page), never 9 - 1
  });
});
