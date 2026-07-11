import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { SearchSelect, type SearchOption } from "./SearchSelect";

/**
 * The shared searchable-combobox used by every entity dropdown (model / agent / repo / …). Single and multi
 * share one look; the stored value is always ids. These tests pin the behaviour both modes rely on so the
 * thin per-entity wrappers only need to prove their option mapping.
 */
const opts: SearchOption[] = [
  { id: "a", label: "Alpha", meta: "one" },
  { id: "b", label: "Beta", meta: "two" },
  { id: "c", label: "Gamma", meta: "three" },
];

describe("SearchSelect — single", () => {
  it("shows the search input when empty and emits [id] on pick", () => {
    const onChange = vi.fn();
    render(<SearchSelect options={opts} value={[]} onChange={onChange} placeholder="Pick…" />);

    fireEvent.focus(screen.getByRole("textbox", { name: "Pick…" }));
    fireEvent.mouseDown(screen.getByRole("option", { name: /Beta/ }));
    expect(onChange).toHaveBeenCalledWith(["b"]);   // single = array of one
  });

  it("renders the chosen value as a chip but keeps the input so you can switch directly", () => {
    const onChange = vi.fn();
    render(<SearchSelect options={opts} value={["a"]} onChange={onChange} placeholder="Pick…" />);

    expect(screen.getByText("Alpha")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Alpha" })).toBeInTheDocument();

    // No remove-first needed: picking a different option replaces the single value.
    fireEvent.focus(screen.getByRole("textbox", { name: "Pick…" }));
    fireEvent.mouseDown(screen.getByRole("option", { name: /Beta/ }));
    expect(onChange).toHaveBeenCalledWith(["b"]);   // replaced, not appended
  });

  it("keeps a saved id no longer in options visible as an 'Unavailable' chip", () => {
    render(<SearchSelect options={opts} value={["gone"]} onChange={() => {}} />);
    expect(screen.getByText("Unavailable")).toBeInTheDocument();   // never silently blanked
  });
});

describe("SearchSelect — multi", () => {
  it("renders selected chips and adds another on pick", () => {
    const onChange = vi.fn();
    render(<SearchSelect multi options={opts} value={["a"]} onChange={onChange} placeholder="Search…" hint="pick some" />);

    expect(screen.getByText("Alpha")).toBeInTheDocument();
    expect(screen.getByText("pick some")).toBeInTheDocument();     // hint line

    fireEvent.focus(screen.getByRole("textbox", { name: "Search…" }));
    fireEvent.mouseDown(screen.getByRole("option", { name: /Gamma/ }));
    expect(onChange).toHaveBeenCalledWith(["a", "c"]);             // appended, not replaced
  });

  it("removes a chip by id", () => {
    const onChange = vi.fn();
    render(<SearchSelect multi options={opts} value={["a", "b"]} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: "Remove Alpha" }));
    expect(onChange).toHaveBeenCalledWith(["b"]);
  });

  it("filters options by label or meta", () => {
    render(<SearchSelect multi options={opts} value={[]} onChange={() => {}} placeholder="Search…" />);

    const input = screen.getByRole("textbox", { name: "Search…" });
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: "two" } });   // matches Beta's meta
    expect(screen.getByRole("option", { name: /Beta/ })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Gamma/ })).toBeNull();
  });
});
