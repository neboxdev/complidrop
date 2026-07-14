using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Data;

internal static class ModelConfiguration
{
    public static void ApplyStructure(ModelBuilder builder)
    {
        builder.Entity<Organization>(e =>
        {
            e.Property(o => o.Name).HasMaxLength(200);
            e.Property(o => o.Industry).HasMaxLength(100);
            e.Property(o => o.CompanySize).HasMaxLength(20);
            e.Property(o => o.TimeZone).HasMaxLength(64).HasDefaultValue("America/New_York");
            e.Property(o => o.State).HasMaxLength(10);
            e.Property(o => o.RegulatoryFactsJson).HasColumnType("jsonb");
        });

        builder.Entity<User>(e =>
        {
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.PasswordHash).HasMaxLength(500);
            e.Property(u => u.FullName).HasMaxLength(200);
            e.Property(u => u.Role).HasMaxLength(50).HasDefaultValue("admin");
            e.HasIndex(u => u.Email).IsUnique();
            // Store-generated default so EVERY existing row gets a non-empty stamp
            // on migration (gen_random_uuid()), and rows inserted without an
            // explicit stamp still get one. Register sets it explicitly. (#202)
            e.Property(u => u.SecurityStamp).HasDefaultValueSql("gen_random_uuid()");
            e.HasOne(u => u.Organization).WithMany(o => o.Users)
                .HasForeignKey(u => u.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<EmailVerificationToken>(e =>
        {
            // SHA-256 hex is a fixed 64 chars. Unique so a verify lookup is a
            // single index seek and a (statistically impossible) hash collision
            // surfaces as a write conflict rather than silent ambiguity.
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.NewEmail).HasMaxLength(256);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            // Cascade so a genuine HARD delete of a user takes its tokens with it
            // (FK integrity). Note: #183 account deletion is a SOFT delete (ADR
            // 0013) — it never fires this cascade; the soft-delete query filter
            // makes the tokens unreachable instead.
            e.HasOne(t => t.User).WithMany()
                .HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PasswordResetToken>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.HasOne(t => t.User).WithMany()
                .HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Vendor>(e =>
        {
            e.Property(v => v.Name).HasMaxLength(200);
            e.Property(v => v.ContactEmail).HasMaxLength(256);
            e.Property(v => v.ContactPhone).HasMaxLength(50);
            e.Property(v => v.Category).HasMaxLength(100);
            e.Property(v => v.EntityType).HasMaxLength(50);
            e.Property(v => v.RegulatoryFactsJson).HasColumnType("jsonb");
            e.HasIndex(v => new { v.OrganizationId, v.Name });
            e.HasOne(v => v.Organization).WithMany(o => o.Vendors)
                .HasForeignKey(v => v.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(v => v.ComplianceTemplate).WithMany(ct => ct.Vendors)
                .HasForeignKey(v => v.ComplianceTemplateId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<VendorPortalLink>(e =>
        {
            e.Property(l => l.Token).HasMaxLength(200);
            e.Property(l => l.UploadCount).HasDefaultValue(0);
            e.Property(l => l.MaxUploads).HasDefaultValue(20);
            e.HasIndex(l => l.Token).IsUnique();
            e.HasOne(l => l.Vendor).WithMany(v => v.PortalLinks)
                .HasForeignKey(l => l.VendorId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Document>(e =>
        {
            e.Property(d => d.OriginalFileName).HasMaxLength(500);
            e.Property(d => d.BlobStorageUrl).HasMaxLength(500);
            e.Property(d => d.BlobStoragePath).HasMaxLength(500);
            e.Property(d => d.ContentType).HasMaxLength(100);
            e.Property(d => d.DocumentType).HasMaxLength(100).HasDefaultValue("other");
            e.Property(d => d.DocumentSubType).HasMaxLength(100);
            e.Property(d => d.UploadedBy).HasMaxLength(50);
            e.Property(d => d.ExtractionStatus).HasConversion<string>().HasMaxLength(50);
            e.Property(d => d.ComplianceStatus).HasConversion<string>().HasMaxLength(50);
            e.Property(d => d.ExtractionPromptVersion).HasMaxLength(100);
            e.Property(d => d.ExtractionRawJson).HasColumnType("jsonb");
            e.Property(d => d.ExtractionFields).HasColumnType("jsonb");
            e.Property(d => d.GeneralLiabilityLimit).HasColumnType("numeric(18,2)");
            e.Property(d => d.ProcessingError).HasMaxLength(2000);
            e.Ignore(d => d.IsExpired);
            e.Ignore(d => d.DaysUntilExpiry);

            e.HasIndex(d => new { d.OrganizationId, d.ExpirationDate });
            e.HasIndex(d => new { d.OrganizationId, d.VendorId });
            e.HasIndex(d => new { d.OrganizationId, d.ComplianceStatus });
            e.HasIndex(d => new { d.OrganizationId, d.ExtractionStatus, d.CreatedAt });
            // At most one LIVE sample-demo document per org (#238). The seed endpoint already dedups on
            // an existence check, but this partial unique index closes the concurrent-double-click
            // TOCTOU race at the database: a racing second insert fails loudly (23505) and the endpoint
            // returns the existing sample instead of double-seeding. Scoped to live sample rows so a
            // normal upload (IsSample=false) is never constrained and a cleared/soft-deleted sample
            // doesn't block re-seeding. Mirrors the IX_ComplianceTemplates_Name_SystemUnique guard.
            e.HasIndex(d => d.OrganizationId)
                .IsUnique()
                .HasFilter("\"IsSample\" AND \"DeletedAt\" IS NULL")
                .HasDatabaseName("IX_Documents_OrganizationId_SampleUnique");

            // ExtractionWorker's claim/zombie-reclaim query (ExtractionWorker.ClaimSql) scans for the
            // next processable document SYSTEM-WIDE — it has NO OrganizationId predicate (a single
            // shared extraction queue across all tenants) — so none of the org-leading indexes above
            // can serve it. Without a dedicated index it degrades to a full "Documents" sequential scan
            // + sort on every 5-second poll, per worker instance, scaling with total table size rather
            // than queue depth — and terminal rows are never deleted (ADR 0013), so the table only
            // grows. A partial index on "CreatedAt" over just the in-flight statuses stays tiny
            // (terminal rows excluded) and satisfies the `ORDER BY "CreatedAt" … LIMIT 1` as a forward
            // index range scan. Covers both claim arms: the Pending claim and the stale-Processing
            // zombie reclaim (the `ProcessingStartedAt < now() - interval '5 minutes'` predicate is a
            // cheap residual filter once the index has narrowed to the handful of in-flight rows).
            // (#243 concurrency-audit performance finding.)
            e.HasIndex(d => d.CreatedAt)
                .HasFilter("\"DeletedAt\" IS NULL AND \"ExtractionStatus\" IN ('Pending', 'Processing')")
                .HasDatabaseName("IX_Documents_ExtractionQueue");

            // Serves the document-supersession correlated EXISTS (DocumentSupersession, #327): for a
            // candidate doc, "does a NEWER doc exist for the same (VendorId, DocumentType)?" The request
            // paths (AppDbContext) carry an OrganizationId predicate too, but the REMINDER worker runs on
            // SystemDbContext with NO tenant filter, so the (OrganizationId, VendorId) index can't seek the
            // inner VendorId lookup (leading column unconstrained) — without a VendorId-leading index it
            // would seq-scan the whole (cross-org, never-pruned) Documents table per candidate, hourly.
            // VendorId-leading + DocumentType + CreatedAt serves the seek + range in one index, and FULLY
            // covers the Vendor FK (so EF drops the now-redundant single-column IX_Documents_VendorId).
            // NOT partial, deliberately: a partial index would not cover the FK, so EF would wrongly drop
            // the full FK index and leave it uncovered. (#327 review.)
            e.HasIndex(d => new { d.VendorId, d.DocumentType, d.CreatedAt })
                .HasDatabaseName("IX_Documents_Supersession");

            e.HasOne(d => d.Organization).WithMany(o => o.Documents)
                .HasForeignKey(d => d.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.Vendor).WithMany(v => v.Documents)
                .HasForeignKey(d => d.VendorId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<DocumentField>(e =>
        {
            e.Property(f => f.FieldName).HasMaxLength(200);
            e.Property(f => f.FieldValue).HasMaxLength(2000);
            e.Property(f => f.FieldType).HasMaxLength(50);
            e.Property(f => f.OriginalValue).HasMaxLength(2000);
            e.HasOne(f => f.Document).WithMany(d => d.Fields)
                .HasForeignKey(f => f.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ComplianceTemplate>(e =>
        {
            e.Property(ct => ct.Name).HasMaxLength(200);
            e.Property(ct => ct.Description).HasMaxLength(500);
            // Seed re-grade durability watermark (#416, ADR 0036 Amendment 2). Store-side default 0 so the
            // additive migration back-fills every existing row (and the system seed) at 0/0 — an already-
            // caught-up state that triggers no re-grade on the first boot after deploy.
            e.Property(ct => ct.RulesRevision).HasDefaultValue(0);
            e.Property(ct => ct.RegradedThroughRevision).HasDefaultValue(0);
            e.HasOne(ct => ct.Organization).WithMany(o => o.ComplianceTemplates)
                .HasForeignKey(ct => ct.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            // Recurrence guard for #251: the system-template seed is idempotent BY NAME, but nothing
            // stopped a seed/rename mismatch from inserting a second live row per venue type (it did,
            // in the #192 manual-migration era). A partial unique index on Name over LIVE system rows
            // makes any future duplication fail loudly at write time instead of silently doubling the
            // "Suggested checklists" list. Scoped to IsSystemTemplate (org templates may legitimately
            // reuse a name) and DeletedAt IS NULL (a soft-deleted system row must not block re-seeding).
            e.HasIndex(ct => ct.Name)
                .IsUnique()
                .HasFilter("\"IsSystemTemplate\" AND \"DeletedAt\" IS NULL")
                .HasDatabaseName("IX_ComplianceTemplates_Name_SystemUnique");
        });

        builder.Entity<ComplianceRule>(e =>
        {
            e.Property(r => r.DocumentType).HasMaxLength(100);
            e.Property(r => r.FieldName).HasMaxLength(200);
            e.Property(r => r.Operator).HasMaxLength(50);
            e.Property(r => r.ExpectedValue).HasMaxLength(500);
            e.Property(r => r.ErrorMessage).HasMaxLength(500);
            e.HasOne(r => r.ComplianceTemplate).WithMany(ct => ct.Rules)
                .HasForeignKey(r => r.ComplianceTemplateId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ComplianceCheck>(e =>
        {
            e.Property(c => c.ActualValue).HasMaxLength(500);
            e.Property(c => c.Notes).HasMaxLength(500);
            e.HasOne(c => c.Document).WithMany(d => d.ComplianceChecks)
                .HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.ComplianceRule).WithMany()
                .HasForeignKey(c => c.ComplianceRuleId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Reminder>(e =>
        {
            e.Property(r => r.EmailSubjectTemplate).HasMaxLength(500);
            e.Property(r => r.EmailBodyTemplate).HasMaxLength(4000);
            e.HasOne(r => r.Organization).WithMany(o => o.Reminders)
                .HasForeignKey(r => r.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReminderLog>(e =>
        {
            e.Property(l => l.RecipientEmail).HasMaxLength(256);
            e.Property(l => l.ResendMessageId).HasMaxLength(200);
            e.Property(l => l.Status).HasMaxLength(50).HasDefaultValue(ReminderLogStatus.Sent);
            // Dedupe is per-recipient: a multi-recipient reminder (internal + vendor) needs one
            // row per recipient on the same day. Narrowing the key to (Reminder, Doc, Date) only
            // would block the second insert and roll back the whole batch, dropping all rows and
            // re-sending on the next tick.
            e.HasIndex(l => new { l.ReminderId, l.DocumentId, l.SendDate, l.RecipientEmail }).IsUnique();
            // Inbound Resend (Svix) webhook looks up the log by its Resend message id.
            e.HasIndex(l => l.ResendMessageId);
            // Org-scoped most-recent history (GET /api/reminders/history): WHERE OrganizationId = @org
            // ORDER BY SentAt DESC LIMIT 200. The composite with SentAt DESC turns this into a forward
            // index range scan — no sort, no whole-table scan, no parent join (#309).
            e.HasIndex(l => new { l.OrganizationId, l.SentAt })
                .IsDescending(false, true)
                .HasDatabaseName("IX_ReminderLogs_OrganizationId_SentAt");
            e.HasOne(l => l.Reminder).WithMany(r => r.Logs)
                .HasForeignKey(l => l.ReminderId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Subscription>(e =>
        {
            e.Property(s => s.StripeCustomerId).HasMaxLength(200);
            e.Property(s => s.StripeSubscriptionId).HasMaxLength(200);
            e.Property(s => s.Plan).HasMaxLength(50).HasDefaultValue("free");
            e.Property(s => s.Status).HasMaxLength(50).HasDefaultValue("active");
            e.Property(s => s.ExtractionSpendThisMonthUsd).HasColumnType("numeric(12,4)").HasDefaultValue(0m);
            // DateOnly.MinValue = "always stale": pre-#256 rows (whose counter held LIFETIME
            // spend) and fresh rows both start with an anchor that can never equal the current
            // month, so their counter reads as zero until the first post-#256 spend re-anchors it.
            e.Property(s => s.SpendMonthStart).HasDefaultValue(DateOnly.MinValue);
            e.HasIndex(s => s.StripeCustomerId)
                .IsUnique()
                .HasFilter("\"StripeCustomerId\" IS NOT NULL");
            e.HasIndex(s => s.OrganizationId).IsUnique();
            e.HasOne(s => s.Organization).WithOne(o => o.Subscription)
                .HasForeignKey<Subscription>(s => s.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WaitlistEntry>(e =>
        {
            e.Property(w => w.Email).HasMaxLength(256);
            e.Property(w => w.CompanyName).HasMaxLength(200);
            e.Property(w => w.Industry).HasMaxLength(100);
            e.Property(w => w.Source).HasMaxLength(100);
            e.HasIndex(w => w.Email).IsUnique();
        });

        builder.Entity<AuditLog>(e =>
        {
            e.Property(a => a.Action).HasMaxLength(100);
            e.Property(a => a.EntityType).HasMaxLength(100);
            e.Property(a => a.IpAddress).HasMaxLength(64);
            e.Property(a => a.UserAgent).HasMaxLength(500);
            e.Property(a => a.CorrelationId).HasMaxLength(64);
            e.Property(a => a.BeforeJson).HasColumnType("jsonb");
            e.Property(a => a.AfterJson).HasColumnType("jsonb");
            e.HasIndex(a => new { a.OrganizationId, a.CreatedAt });
            e.HasIndex(a => new { a.EntityType, a.EntityId });
        });

        builder.Entity<IdempotencyRecord>(e =>
        {
            e.Property(i => i.Key).HasMaxLength(200);
            e.Property(i => i.RequestPath).HasMaxLength(500);
            e.Property(i => i.ResponseJson).HasColumnType("jsonb");
            e.HasIndex(i => new { i.OrganizationId, i.Key }).IsUnique();
        });

        builder.Entity<ProcessedStripeEvent>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasMaxLength(200);
            e.Property(p => p.Type).HasMaxLength(100);
        });

        builder.Entity<EmailSuppression>(e =>
        {
            e.Property(s => s.Email).HasMaxLength(320); // RFC 5321 max address length
            // One suppression per (org, email). The webhook upserts under this index; the worker reads
            // it per org (#340).
            e.HasIndex(s => new { s.OrganizationId, s.Email }).IsUnique();
        });
    }

    public static void ApplySoftDeleteFilters(ModelBuilder builder)
    {
        builder.Entity<Organization>().HasQueryFilter(o => o.DeletedAt == null);
        builder.Entity<User>().HasQueryFilter(u => u.DeletedAt == null);
        builder.Entity<Vendor>().HasQueryFilter(v => v.DeletedAt == null);
        builder.Entity<Document>().HasQueryFilter(d => d.DeletedAt == null);
        builder.Entity<ComplianceTemplate>().HasQueryFilter(ct => ct.DeletedAt == null);
    }
}
