/**
 * What a removal confirmation is allowed to claim (#398 / ADR 0013 Amendment 1).
 *
 * Nothing in CompliDrop hard-deletes. `DocumentEndpoints.DeleteDocument` and
 * `VendorEndpoints.DeleteVendor` both call `Remove()`, which
 * `AuditSaveChangesInterceptor` translates into a `DeletedAt` stamp, and the
 * document's blob is RETAINED on purpose — "so a soft-deleted customer document
 * remains recoverable and its audit trail keeps a viewable original" is the
 * endpoint's own comment. So a dialog saying *"this can't be undone"* asserts
 * the opposite of what the code is designed to guarantee. What IS true is the
 * scoped claim: the row leaves the customer's records and the customer has no
 * way back — there is no restore affordance anywhere in `frontend/`.
 *
 * Single-sourced for the reason `ExportService.Disclaimer` is (ADR 0047 §1):
 * the document sentence shipped as two hand-copied literals, the list row and
 * the detail header, so a counsel reword under CLM-7 would have landed on one
 * of them. Deliberately NOT a per-dialog retention paragraph — the disclosure
 * of what we keep belongs to `/privacy` § "How long we keep it" and to the
 * account-closure card, where the customer is deciding about all of it at once.
 */

/** `documents/page.tsx` list row + `documents/[id]/page.tsx` header. */
export const DOCUMENT_REMOVAL_NOTICE =
  "This removes the document from your records. You won't be able to undo it.";

/** `vendors/[id]/page.tsx`. The middle sentence is `DeleteVendor`'s link deactivation (#269). */
export const VENDOR_REMOVAL_NOTICE =
  "This removes the vendor and deactivates any upload links you shared with them. " +
  "Documents they already sent stay in your account. You won't be able to undo this.";
