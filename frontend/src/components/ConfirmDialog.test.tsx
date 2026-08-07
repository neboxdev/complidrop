/**
 * ConfirmDialog — accessible confirm flow replacing native confirm() (#189).
 *
 * The fixture below is the SHIPPED document notice, imported rather than typed (#398 round 2 /
 * S2). It used to read "This can't be undone." — the exact claim #398 retired, surviving in the
 * file a developer opens when wiring the next confirm dialog, which is precisely the "dialog on a
 * page nobody has written yet" the census exists for. The census cannot catch it: test files are
 * excluded from its walk by construction (`TEST_FILE_RE`), so nothing would flag the paste until
 * it reached a page. Importing the constant also means a counsel reword under CLM-7 moves this
 * fixture with the product instead of stranding it.
 */
import { describe, it, expect, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { DOCUMENT_REMOVAL_NOTICE } from "@/lib/removal-copy";
import { ConfirmDialog } from "./ConfirmDialog";

function setup(onConfirm = vi.fn()) {
  render(
    <ConfirmDialog
      title="Remove file.pdf?"
      description={DOCUMENT_REMOVAL_NOTICE}
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
    expect(screen.getByText(DOCUMENT_REMOVAL_NOTICE)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /^remove$/i }));
    expect(onConfirm).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());
  });

  it("cancel closes without calling onConfirm, and returns focus to the trigger", async () => {
    const { onConfirm } = setup();
    const trigger = screen.getByRole("button", { name: /remove file\.pdf/i });
    fireEvent.click(trigger);
    await screen.findByRole("alertdialog");

    fireEvent.click(screen.getByRole("button", { name: /cancel/i }));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());
    expect(onConfirm).not.toHaveBeenCalled();
    // Focus returns to the trigger (the native-confirm behavior hand-rolled
    // modals forget) — Base UI restores it on close.
    await waitFor(() => expect(trigger).toHaveFocus());
  });
});
