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

/// <summary>
/// Whether the system stands behind the values this document's compliance verdict was computed from
/// (#459, <see href="../../../docs/adr/0052-extraction-trust-is-its-own-column.md">ADR 0052</see>) —
/// the ADR 0042 distrust signal, on its OWN column so it is not destroyed by a pipeline re-arm.
/// <para/>
/// Orthogonal to <see cref="ExtractionStatus"/>, which is PIPELINE POSITION (where the document sits in
/// the queue). The two used to share one column: <c>ManualRequired</c> meant both "settled" and
/// "distrusted", so <c>DocumentEndpoints.Reextract</c> writing <c>Pending</c> over it erased the distrust
/// with nothing to restore it.
/// <para/>
/// <c>Trusted</c> is the ABSENCE of a distrust event, not a positive endorsement — a freshly-uploaded
/// document nothing has read yet is <c>Trusted</c> here and withheld from affirmative coverage by the
/// never-graded axis instead (ADR 0048).
/// </summary>
public enum ExtractionTrust
{
    /// <summary>No distrust event on record: the last read completed cleanly, or a human confirmed the
    /// values, or nothing has read the document yet.</summary>
    Trusted,

    /// <summary>The last event to touch this document's basis did NOT vouch for it — the machine routed
    /// the read to a human (low average / low verdict-bearing-field confidence, the model's reprocess
    /// signal, or an unreadable canonical value), or the extraction terminally FAILED. Withheld from
    /// in-force vendor coverage (ADR 0042) until a clean re-read or a human confirmation clears it.</summary>
    Distrusted
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

    /// <summary>
    /// Does the system stand behind the values this document's verdict was computed from? See
    /// <see cref="Entities.ExtractionTrust"/>. Written by the four events that establish or undermine the
    /// basis — <c>ExtractionWorker.PersistSuccess</c> (clean read vs routed-to-review),
    /// <c>ExtractionWorker.MarkFailed</c> / <c>RecordFailedAttempt</c> (terminal failure), and
    /// <c>DocumentEndpoints.ResolveManualReview</c> (a human confirmed) — and deliberately NOT by
    /// <c>Reextract</c>, whose whole point is that the distrust must survive the re-arm (#459 / ADR 0052).
    /// The vendor coverage rollup reads THIS, never <see cref="ExtractionStatus"/>.
    /// </summary>
    public ExtractionTrust ExtractionTrust { get; set; } = ExtractionTrust.Trusted;

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
