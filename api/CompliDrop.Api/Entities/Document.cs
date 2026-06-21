using System.Text.Json;

namespace CompliDrop.Api.Entities;

public enum ExtractionStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    ManualRequired
}

public enum ComplianceStatus
{
    Pending,
    Compliant,
    NonCompliant,
    ExpiringSoon,
    Expired
}

public class Document
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? VendorId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string BlobStorageUrl { get; set; } = string.Empty;
    public string? BlobStoragePath { get; set; }
    public long FileSizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;

    public string DocumentType { get; set; } = "other";
    public string? DocumentSubType { get; set; }

    public ExtractionStatus ExtractionStatus { get; set; } = ExtractionStatus.Pending;
    public double? ExtractionConfidence { get; set; }
    public string? ExtractionRawJson { get; set; }
    public JsonDocument? ExtractionFields { get; set; }
    public string? ExtractionPromptVersion { get; set; }
    public DateTime? ExtractionCompletedAt { get; set; }

    public DateTime? ProcessingStartedAt { get; set; }

    /// <summary>
    /// Total times this document has been CLAIMED (incremented in the worker's claim SQL, including
    /// zombie reclaims). A crash-loop backstop — not the retry budget — so a document that kills the
    /// process before any handler runs can't be reclaimed forever. Restarts/deploys bump this, so it
    /// must NOT gate ordinary retries; that's <see cref="FailedAttempts"/>. See ExtractionWorker (#259).
    /// </summary>
    public int ProcessingAttempts { get; set; } = 0;

    /// <summary>
    /// Count of GENUINELY-FAILED attempts (extraction ran and threw, or timed out). This — not
    /// <see cref="ProcessingAttempts"/> — is the retry budget: a restart/deploy that interrupts an
    /// in-flight attempt is not a failure and never bumps this, so deploys alone can no longer fail a
    /// document that never had a real extraction error (#259, problem 2).
    /// </summary>
    public int FailedAttempts { get; set; } = 0;
    public string? ProcessingError { get; set; }

    public DateTime? EffectiveDate { get; set; }
    public DateTime? ExpirationDate { get; set; }

    public decimal? GeneralLiabilityLimit { get; set; }

    public ComplianceStatus ComplianceStatus { get; set; } = ComplianceStatus.Pending;

    public bool IsExpired => ExpirationDate.HasValue && ExpirationDate.Value.Date < DateTime.UtcNow.Date;
    public int? DaysUntilExpiry => ExpirationDate.HasValue
        ? (int)(ExpirationDate.Value.Date - DateTime.UtcNow.Date).TotalDays
        : null;

    public string? UploadedBy { get; set; }
    public bool IsManuallyVerified { get; set; } = false;

    /// <summary>
    /// True only for the one-click sample-certificate demo document (#238). Drives the "Sample"
    /// labels across the documents list/detail UI and lets "Clear sample data" find what to remove.
    /// A normal owner/portal upload is always false; only the sample-seed endpoint sets it.
    /// </summary>
    public bool IsSample { get; set; } = false;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Organization Organization { get; set; } = null!;
    public Vendor? Vendor { get; set; }
    public ICollection<DocumentField> Fields { get; set; } = [];
    public ICollection<ComplianceCheck> ComplianceChecks { get; set; } = [];
}

public class DocumentField
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string? FieldValue { get; set; }
    public string? FieldType { get; set; }
    public double Confidence { get; set; }
    public bool IsManuallyEdited { get; set; } = false;
    public string? OriginalValue { get; set; }

    public Document Document { get; set; } = null!;
}
