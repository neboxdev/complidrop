/**
 * PasswordInput — the show/hide toggle a11y contract (#189).
 */
import { describe, it, expect } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { PasswordInput } from "./PasswordInput";

describe("PasswordInput (#189)", () => {
  it("starts masked and toggles to text, flipping aria-pressed + the accessible name", () => {
    render(<PasswordInput aria-label="Password" defaultValue="hunter2" />);

    const input = screen.getByLabelText("Password") as HTMLInputElement;
    expect(input.type).toBe("password");

    const toggle = screen.getByRole("button", { name: /show password/i });
    expect(toggle).toHaveAttribute("aria-pressed", "false");

    fireEvent.click(toggle);
    expect(input.type).toBe("text");
    // The same button now reads "Hide password" + aria-pressed=true.
    const pressed = screen.getByRole("button", { name: /hide password/i });
    expect(pressed).toHaveAttribute("aria-pressed", "true");

    fireEvent.click(pressed);
    expect(input.type).toBe("password");
  });

  it("forwards id + aria-describedby to the underlying input (label/error wiring survives)", () => {
    render(
      <>
        <label htmlFor="pw">Password</label>
        <PasswordInput id="pw" aria-describedby="pw-err" />
        <p id="pw-err">Too short</p>
      </>,
    );
    const input = screen.getByLabelText("Password");
    expect(input).toHaveAttribute("id", "pw");
    expect(input).toHaveAttribute("aria-describedby", "pw-err");
  });
});
