using System.Text.RegularExpressions;
using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

/// <summary>
/// Human-facing labels for the enum / type / audit-action values that would
/// otherwise leak into the exported PDF/CSV as raw codes ("NonCompliant",
/// "coi", "compliancetemplate.created"). Mirrors the frontend
/// `frontend/src/lib/display-labels.ts` + `document-types.ts` so the app and the
/// export read identically. Keep the two in lockstep. (#188)
/// </summary>
public static class DisplayLabels
{
    public static string Compliance(ComplianceStatus status) => status switch
    {
        ComplianceStatus.Pending => "Awaiting review",
        ComplianceStatus.Compliant => "Compliant",
        ComplianceStatus.NonCompliant => "Action needed",
        ComplianceStatus.ExpiringSoon => "Expiring soon",
        ComplianceStatus.Expired => "Expired",
        _ => status.ToString(),
    };

    public static string Extraction(ExtractionStatus status) => status switch
    {
        ExtractionStatus.Pending => "Waiting to read",
        ExtractionStatus.Processing => "Reading…",
        ExtractionStatus.Completed => "Read",
        ExtractionStatus.ManualRequired => "Needs your review",
        ExtractionStatus.Failed => "Couldn't read",
        _ => status.ToString(),
    };

    private static readonly IReadOnlyDictionary<string, string> DocumentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["coi"] = "Certificate of Insurance",
            ["license"] = "Business License",
            ["permit"] = "Permit",
            ["certification"] = "Certification",
            ["contract"] = "Contract",
            ["other"] = "Other",
        };

    public static string DocumentType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Other";
        return DocumentTypes.TryGetValue(type.Trim(), out var label) ? label : type;
    }

    // OrdinalIgnoreCase so this resolves BOTH the interceptor's all-lower-case
    // entity actions ("compliancetemplate.created") AND the explicit camelCase
    // ones ("complianceRule.upserted", "vendorPortalLink.revoked"). Keep in sync
    // with the frontend display-labels.ts ACTION_LABELS.
    private static readonly IReadOnlyDictionary<string, string> Actions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["document.created"] = "Document added",
            ["document.uploaded"] = "Document uploaded",
            ["document.updated"] = "Document updated",
            ["document.deleted"] = "Document removed",
            ["document.verified"] = "Document verified",
            ["document.fields_edited"] = "Document details edited",
            ["document.reextract_queued"] = "Document re-read",
            ["document.processed"] = "Document read",
            ["documentfield.created"] = "Document detail added",
            ["documentfield.updated"] = "Document detail edited",
            ["vendor.created"] = "Vendor added",
            ["vendor.updated"] = "Vendor updated",
            ["vendor.deleted"] = "Vendor removed",
            ["vendorportallink.created"] = "Portal link created",
            ["vendorportallink.revoked"] = "Portal link revoked",
            ["vendorportallink.deleted"] = "Portal link revoked",
            ["vendorportallink.emailed"] = "Upload link emailed",
            ["vendorportallink.upload_processed"] = "Vendor sent a document",
            ["compliancetemplate.created"] = "Requirement set created",
            ["compliancetemplate.updated"] = "Requirement set updated",
            ["compliancetemplate.deleted"] = "Requirement set removed",
            ["compliancerule.created"] = "Requirement added",
            ["compliancerule.updated"] = "Requirement updated",
            ["compliancerule.upserted"] = "Requirement saved",
            ["compliancerule.deleted"] = "Requirement removed",
            ["reminder.recipient_suppressed"] = "Reminders paused — bad email",
            ["user.registered"] = "Account created",
            ["user.logged_in"] = "Signed in",
            ["user.login_failed"] = "Sign-in failed",
            ["user.password_changed"] = "Password changed",
            ["user.password_reset"] = "Password reset",
            ["user.password_reset_requested"] = "Password reset requested",
            ["user.email_verified"] = "Email verified",
            ["user.email_changed"] = "Email changed",
            ["user.email_change_requested"] = "Email change requested",
            ["user.account_deleted"] = "Account deleted",
        };

    public static string Action(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return "";
        var key = action.Trim();
        if (Actions.TryGetValue(key, out var label)) return label;
        // Fallback (mirrors the frontend): de-camelCase, de-dot, de-snake, title-case
        // so an unmapped action is never printed as a raw code in the audit PDF.
        var s = Regex.Replace(key, "([a-z0-9])([A-Z])", "$1 $2")
            .Replace(".", " · ")
            .Replace("_", " ");
        return Regex.Replace(s, @"\b\w", m => m.Value.ToUpperInvariant());
    }

    private static readonly IReadOnlyDictionary<string, string> EntityTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Document"] = "Document",
            ["DocumentField"] = "Document detail",
            ["Vendor"] = "Vendor",
            ["VendorPortalLink"] = "Portal link",
            ["ComplianceTemplate"] = "Requirement set",
            ["ComplianceRule"] = "Requirement",
            ["User"] = "Account",
            ["Organization"] = "Organization",
        };

    /// <summary>Friendly label for an AuditLog.EntityType (the raw entity class name).</summary>
    public static string EntityType(string? entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType)) return "";
        return EntityTypes.TryGetValue(entityType.Trim(), out var label) ? label : entityType;
    }
}
