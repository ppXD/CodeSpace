import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { StatBlock } from "../../api/sessions";
import { StatRow } from "./SessionRoomView";

describe("Room durable log truth", () => {
  it("renders every agent and preserves backend-authored health tones", () => {
    const stat: StatBlock = {
      id: "turn-1:stat:logs",
      seq: 10,
      type: "stat",
      kind: "logs",
      label: "Logs",
      detail: "3 streams · incomplete",
      items: [
        { text: "API", detail: "1 stream · 1 integrity verified", tone: "Success" },
        { text: "Web", detail: "2 streams · 1 capture failed · 1 finalizing", tone: "Error" },
      ],
    };

    const { container } = render(<StatRow stat={stat} />);
    expect(screen.getByText("3 streams · incomplete")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /Logs/ }));

    expect(screen.getByText("API")).toBeInTheDocument();
    expect(screen.getByText("Web")).toBeInTheDocument();
    expect(screen.getByText("1 stream · 1 integrity verified")).toHaveClass("room-good");
    expect(screen.getByText("2 streams · 1 capture failed · 1 finalizing")).toHaveClass("room-danger");
    expect(container.querySelector(".room-row-ic")).toBeTruthy();
  });
});
