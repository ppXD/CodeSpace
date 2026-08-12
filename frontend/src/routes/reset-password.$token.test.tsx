import { fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { renderRoute, stubFetch } from "@/test/route-harness";

/**
 * The reset page is reachable by someone who cannot sign in — that is the whole point of it — so what
 * is worth pinning is that it works without a session, and that spending the link lands them at
 * sign-in rather than inside the app.
 */
describe("password reset", () => {
  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("is reachable without a session", async () => {
    // If the auth gate ever swallows this route, the only people who need it are the only people who
    // cannot reach it.
    stubFetch({});

    const { router } = await renderRoute("/reset-password/some-token");

    await waitFor(() => expect(screen.getByText("Set a new password")).toBeTruthy());
    expect(router.state.location.pathname).toBe("/reset-password/some-token");
  });

  it("sends the visitor to sign in rather than into the app", async () => {
    // Spending the link ends every session the account had. Landing them inside the product would
    // mean a session was handed back, which would undo the revocation for whoever prompted the reset.
    stubFetch({ "/api/auth/reset-password/": {} });

    await renderRoute("/reset-password/some-token");
    await waitFor(() => expect(screen.getByText("Set a new password")).toBeTruthy());

    const [password, confirm] = screen.getAllByDisplayValue("");
    fireEvent.change(password, { target: { value: "long-enough-passphrase" } });
    fireEvent.change(confirm, { target: { value: "long-enough-passphrase" } });
    fireEvent.click(screen.getByRole("button", { name: "Set password" }));

    await waitFor(() => expect(screen.getByText("Password changed")).toBeTruthy());
    expect(screen.getByRole("button", { name: "Go to sign in" })).toBeTruthy();
  });

  it("refuses a password shorter than the server would accept", async () => {
    // Told before the request, not after a round trip that answers 400.
    stubFetch({});

    await renderRoute("/reset-password/some-token");
    await waitFor(() => expect(screen.getByText("Set a new password")).toBeTruthy());

    const [password, confirm] = screen.getAllByDisplayValue("");
    fireEvent.change(password, { target: { value: "short" } });
    fireEvent.change(confirm, { target: { value: "short" } });

    expect(screen.getByText(/At least 12 characters/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Set password" }).hasAttribute("disabled")).toBe(true);
  });
});
