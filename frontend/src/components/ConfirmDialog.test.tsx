/**
 * ConfirmDialog — accessible confirm flow replacing native confirm() (#189).
 */
import { describe, it, expect, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { ConfirmDialog } from "./ConfirmDialog";

function setup(onConfirm = vi.fn()) {
  render(
    <ConfirmDialog
      title="Remove file.pdf?"
      description="This can't be undone."
      confirmLabel="Remove"
      destructive
      onConfirm={onConfirm}
      trigger={<button aria-label="Remove file.pdf">x</button>}
    />,
  );
  return { onConfirm };
}

describe("ConfirmDialog (#189)", () => {
  it("opens on trigger, shows a labelled dialog, and confirms", async () => {
    const { onConfirm } = setup();
    // Dialog not open initially.
    expect(screen.queryByRole("alertdialog")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /remove file\.pdf/i }));

    const dialog = await screen.findByRole("alertdialog");
    // Labelled by its heading (Base UI wires aria-labelledby to the Title).
    expect(dialog).toHaveAccessibleName(/remove file\.pdf\?/i);
    expect(screen.getByText(/can't be undone/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /^remove$/i }));
    expect(onConfirm).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());
  });

  it("cancel closes without calling onConfirm", async () => {
    const { onConfirm } = setup();
    fireEvent.click(screen.getByRole("button", { name: /remove file\.pdf/i }));
    await screen.findByRole("alertdialog");

    fireEvent.click(screen.getByRole("button", { name: /cancel/i }));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());
    expect(onConfirm).not.toHaveBeenCalled();
  });
});
