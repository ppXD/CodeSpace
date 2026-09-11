import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { StatBlock } from "../../api/sessions";
import { StatRow } from "./SessionRoomView";

describe("Room quality-policy row", () => {
  // P22-9b. The backend authors this row as an ordinary StatBlock under a new open `kind`, so what is worth
  // asserting here is that the OPEN-KIND contract actually holds end to end: an itemized row the frontend was
  // never taught about still renders every unit's mechanism AND the evidence beside it. A mechanism with its
  // reason dropped would be a verdict an operator has to take on trust.
  it("renders each unit's mechanism with the evidence that chose it", () => {
    const stat: StatBlock = {
      id: "turn-1:stat:quality",
      seq: 12,
      type: "stat",
      kind: "quality",
      label: "Quality policy",
      detail: "recommended for 2 units",
      items: [
        { text: "s1 · EscalateModel", detail: "the check failed on 2 consecutive attempts over a localized diff" },
        { text: "s2 · Stop", detail: "a human authorized forgoing verification for this work" },
      ],
    };

    const { container } = render(<StatRow stat={stat} />);
    expect(screen.getByText("recommended for 2 units")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /Quality policy/ }));

    expect(screen.getByText("s1 · EscalateModel")).toBeInTheDocument();
    expect(screen.getByText("the check failed on 2 consecutive attempts over a localized diff")).toBeInTheDocument();
    expect(screen.getByText("s2 · Stop")).toBeInTheDocument();
    expect(screen.getByText("a human authorized forgoing verification for this work")).toBeInTheDocument();
    expect(container.querySelector(".room-row-ic")).toBeTruthy();
  });
});
