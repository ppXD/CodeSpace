import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { StorageSettings } from "./StorageSettings";

describe("storage settings foundation", () => {
  it("states the additive storage boundary without exposing an unimplemented mutation", () => {
    render(<StorageSettings />);

    expect(screen.getByRole("heading", { name: "Artifact storage" })).toBeTruthy();
    expect(screen.getByText(/Existing workflow runs continue to use the deployment-managed artifact store/)).toBeTruthy();
    expect(screen.getByText(/does not change where current run artifacts, model calls, or logs are written/)).toBeTruthy();
    expect(screen.queryByRole("button")).toBeNull();
    expect(screen.queryByRole("textbox")).toBeNull();
  });
});
