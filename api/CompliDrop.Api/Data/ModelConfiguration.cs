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
        });

        builder.Entity<User>(e =>
        {
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.PasswordHash).HasMaxLength(500);
            e.Property(u => u.FullName).HasMaxLength(200);
            e.Property(u => u.Role).HasMaxLength(50).HasDefaultValue("admin");
            e.HasIndex(u => u.Email).IsUnique();
            e.HasOne(u => u.Organization).WithMany(o => o.Users)
                .HasForeignKey(u => u.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Vendor>(e =>
        {
            e.Property(v => v.Name).HasMaxLength(200);
            e.Property(v => v.ContactEmail).HasMaxLength(256);
            e.Property(v => v.ContactPhone).HasMaxLength(50);
            e.Property(v => v.Category).HasMaxLength(100);
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
            e.HasOne(ct => ct.Organization).WithMany(o => o.ComplianceTemplates)
                .HasForeignKey(ct => ct.OrganizationId).OnDelete(DeleteBehavior.Cascade);
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
            e.Property(l => l.Status).HasMaxLength(50).HasDefaultValue("sent");
            e.HasIndex(l => new { l.ReminderId, l.DocumentId, l.SendDate }).IsUnique();
            // Inbound Resend (Svix) webhook looks up the log by its Resend message id.
            e.HasIndex(l => l.ResendMessageId);
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
