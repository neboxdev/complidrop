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
            ["vendor.created"] = "Vendor added",
            ["vendor.updated"] = "Vendor updated",
            ["vendor.deleted"] = "Vendor removed",
            ["vendorportallink.created"] = "Portal link created",
            ["vendorportallink.deleted"] = "Portal link revoked",
            ["compliancetemplate.created"] = "Requirement set created",
            ["compliancetemplate.updated"] = "Requirement set updated",
            ["compliancetemplate.deleted"] = "Requirement set removed",
            ["compliancerule.created"] = "Requirement added",
            ["compliancerule.updated"] = "Requirement updated",
            ["compliancerule.deleted"] = "Requirement removed",
            ["user.registered"] = "Account created",
            ["user.login"] = "Signed in",
        };

    public static string Action(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return "";
        return Actions.TryGetValue(action.Trim(), out var label) ? label : action;
    }
}
