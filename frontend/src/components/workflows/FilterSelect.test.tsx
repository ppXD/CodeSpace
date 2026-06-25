import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { FilterSelect, type FilterOption } from "./FilterSelect";

const OPTS: FilterOption[] = [
  { value: "r1", label: "acme/api" },
  { value: "r2", label: "acme/web" },
  { value: "r3", label: "acme/cli" },
];

function renderSel(values: string[] = []) {
  const onChange = vi.fn();
  render(<FilterSelect label="Repository" options={OPTS} values={values} onChange={onChange} />);
  return onChange;
}

describe("FilterSelect (multi-select)", () => {
  it("shows a caret when empty and a coral count when armed", () => {
    const { rerender } = render(<FilterSelect label="Repository" options={OPTS} values={[]} onChange={() => {}} />);
    expect(document.querySelector(".filterpill-count")).toBeNull();

    rerender(<FilterSelect label="Repository" options={OPTS} values={["r1", "r2"]} onChange={() => {}} />);
    expect(document.querySelector(".filterpill-count")?.textContent).toBe("2");
  });

  it("opens a checkbox list with the already-chosen values ticked", () => {
    renderSel(["r1"]);
    fireEvent.click(screen.getByText("Repository"));

    expect(screen.getByRole("option", { name: /acme\/api/ }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("option", { name: /acme\/web/ }).getAttribute("aria-selected")).toBe("false");
  });

  it("ticking an unchosen row ADDS its value and keeps the popover open for more picks", () => {
    const onChange = renderSel(["r1"]);
    fireEvent.click(screen.getByText("Repository"));

    fireEvent.click(screen.getByText("acme/web"));            // add r2
    expect(onChange).toHaveBeenLastCalledWith(["r1", "r2"]);
    expect(screen.getByRole("listbox")).toBeInTheDocument();  // stays open — multi-select
  });

  it("re-ticking a chosen value removes just it from a multi-value set", () => {
    const onChange = renderSel(["r1", "r2"]);                 // both chosen — exercises removal from a real 2-element array
    fireEvent.click(screen.getByText("Repository"));

    fireEvent.click(screen.getByText("acme/api"));            // toggle r1 off
    expect(onChange).toHaveBeenLastCalledWith(["r2"]);
  });

  it("the header shows the count and Clear empties the facet", () => {
    const onChange = renderSel(["r1", "r2"]);
    fireEvent.click(screen.getByText("Repository"));

    expect(screen.getByText("2 selected")).toBeInTheDocument();
    fireEvent.click(screen.getByText("Clear"));
    expect(onChange).toHaveBeenCalledWith([]);
  });

  it("closes on Escape", () => {
    renderSel();
    fireEvent.click(screen.getByText("Repository"));
    expect(screen.getByRole("listbox")).toBeInTheDocument();

    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("closes on an outside click", () => {
    renderSel();
    fireEvent.click(screen.getByText("Repository"));
    expect(screen.getByRole("listbox")).toBeInTheDocument();

    fireEvent.mouseDown(document.body);
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("shows a search box only past the threshold and filters the list by it", () => {
    const many: FilterOption[] = Array.from({ length: 8 }, (_, i) => ({ value: `v${i}`, label: `repo-${i}` }));
    render(<FilterSelect label="Repository" options={many} values={[]} onChange={() => {}} />);
    fireEvent.click(screen.getByText("Repository"));

    fireEvent.change(screen.getByLabelText("Search Repository"), { target: { value: "repo-3" } });
    expect(screen.getByText("repo-3")).toBeInTheDocument();
    expect(screen.queryByText("repo-2")).toBeNull();
  });
});
