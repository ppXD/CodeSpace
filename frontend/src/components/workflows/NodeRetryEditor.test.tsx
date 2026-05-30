import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { RetryPolicy } from "@/api/workflows";
import { NodeRetryEditor, RETRY_MAX_ATTEMPTS_CAP, RETRY_MAX_BACKOFF_SECONDS } from "./NodeRetryEditor";

/**
 * NodeRetryEditor is the per-node "retry on failure" control in the editor inspector. These pin:
 *   1. off by default (no policy) — just the toggle, no fields;
 *   2. toggling on seeds a policy that actually retries (maxAttempts > 1);
 *   3. toggling off clears the policy (emits null → saved definition omits retry);
 *   4. editing maxAttempts / backoff emits a patched policy;
 *   5. values are clamped to the engine caps so the editor can't emit an unsavable policy.
 */
describe("NodeRetryEditor", () => {
  it("renders just the toggle (unchecked) when there is no policy", () => {
    render(<NodeRetryEditor value={null} onChange={vi.fn()} />);

    expect((screen.getByRole("checkbox") as HTMLInputElement).checked).toBe(false);
    expect(screen.queryByRole("spinbutton", { name: /max attempts/i })).toBeNull();
  });

  it("seeds a retrying policy when toggled on", () => {
    const onChange = vi.fn();
    render(<NodeRetryEditor value={null} onChange={onChange} />);

    fireEvent.click(screen.getByRole("checkbox"));

    expect(onChange).toHaveBeenCalledWith({ maxAttempts: 3, backoffSeconds: 0 });
    expect((onChange.mock.calls[0][0] as RetryPolicy).maxAttempts).toBeGreaterThan(1);
  });

  it("clears the policy when toggled off", () => {
    const onChange = vi.fn();
    render(<NodeRetryEditor value={{ maxAttempts: 3, backoffSeconds: 2 }} onChange={onChange} />);

    expect((screen.getByRole("checkbox") as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole("checkbox"));

    expect(onChange).toHaveBeenCalledWith(null);
  });

  it("patches maxAttempts and backoff", () => {
    const onChange = vi.fn();
    render(<NodeRetryEditor value={{ maxAttempts: 3, backoffSeconds: 0 }} onChange={onChange} />);

    fireEvent.change(screen.getByRole("spinbutton", { name: /max attempts/i }), { target: { value: "5" } });
    expect(onChange).toHaveBeenLastCalledWith({ maxAttempts: 5, backoffSeconds: 0 });

    fireEvent.change(screen.getByRole("spinbutton", { name: /backoff/i }), { target: { value: "2.5" } });
    expect(onChange).toHaveBeenLastCalledWith({ maxAttempts: 3, backoffSeconds: 2.5 });
  });

  it("clamps values to the engine caps so the editor can't emit an unsavable policy", () => {
    const onChange = vi.fn();
    render(<NodeRetryEditor value={{ maxAttempts: 3, backoffSeconds: 0 }} onChange={onChange} />);

    fireEvent.change(screen.getByRole("spinbutton", { name: /max attempts/i }), { target: { value: "999" } });
    expect(onChange).toHaveBeenLastCalledWith({ maxAttempts: RETRY_MAX_ATTEMPTS_CAP, backoffSeconds: 0 });

    fireEvent.change(screen.getByRole("spinbutton", { name: /max attempts/i }), { target: { value: "0" } });
    expect(onChange).toHaveBeenLastCalledWith({ maxAttempts: 1, backoffSeconds: 0 });

    fireEvent.change(screen.getByRole("spinbutton", { name: /backoff/i }), { target: { value: "999" } });
    expect(onChange).toHaveBeenLastCalledWith({ maxAttempts: 3, backoffSeconds: RETRY_MAX_BACKOFF_SECONDS });
  });
});
