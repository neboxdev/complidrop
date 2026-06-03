using CompliDrop.Api.Auth;
using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Data;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ICurrentUser currentUser) : DbContext(options)
{
    // Instance member — EF re-evaluates this per query execution instead of baking
    // a closure into the cached model. Lets different request scopes with different
    // ICurrentUser implementations all share the same compiled model.
    public Guid CurrentOrgId => currentUser.OrganizationId ?? Guid.Empty;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<EmailVerificationToken> EmailVerificationTokens => Set<EmailVerificationToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorPortalLink> VendorPortalLinks => Set<VendorPortalLink>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentField> DocumentFields => Set<DocumentField>();
    public DbSet<ComplianceTemplate> ComplianceTemplates => Set<ComplianceTemplate>();
    public DbSet<ComplianceRule> ComplianceRules => Set<ComplianceRule>();
    public DbSet<ComplianceCheck> ComplianceChecks => Set<ComplianceCheck>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<ReminderLog> ReminderLogs => Set<ReminderLog>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents => Set<ProcessedStripeEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        ModelConfiguration.ApplyStructure(builder);

        builder.Entity<Organization>().HasQueryFilter(o =>
            o.DeletedAt == null && o.Id == CurrentOrgId);
        builder.Entity<User>().HasQueryFilter(u =>
            u.DeletedAt == null && u.OrganizationId == CurrentOrgId);
        builder.Entity<Vendor>().HasQueryFilter(v =>
            v.DeletedAt == null && v.OrganizationId == CurrentOrgId);
        builder.Entity<Document>().HasQueryFilter(d =>
            d.DeletedAt == null && d.OrganizationId == CurrentOrgId);
        builder.Entity<ComplianceTemplate>().HasQueryFilter(ct =>
            ct.DeletedAt == null && (ct.IsSystemTemplate || ct.OrganizationId == CurrentOrgId));
        builder.Entity<Reminder>().HasQueryFilter(r => r.OrganizationId == CurrentOrgId);
        builder.Entity<Subscription>().HasQueryFilter(s => s.OrganizationId == CurrentOrgId);
        builder.Entity<AuditLog>().HasQueryFilter(a => a.OrganizationId == CurrentOrgId);
        builder.Entity<IdempotencyRecord>().HasQueryFilter(i => i.OrganizationId == CurrentOrgId);
    }
}
