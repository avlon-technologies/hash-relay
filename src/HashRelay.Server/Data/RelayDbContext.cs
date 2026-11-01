using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Data;

public sealed class RelayDbContext(DbContextOptions<RelayDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<Endpoint> Endpoints => Set<Endpoint>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DevicePairing> DevicePairings => Set<DevicePairing>();
    public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();
    public DbSet<OAuthGrant> OAuthGrants => Set<OAuthGrant>();
    public DbSet<OAuthCode> OAuthCodes => Set<OAuthCode>();
    public DbSet<OAuthRefreshToken> OAuthRefreshTokens => Set<OAuthRefreshToken>();
    public DbSet<PasswordReset> PasswordResets => Set<PasswordReset>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<ApprovalAudit> ApprovalAudits => Set<ApprovalAudit>();
    public DbSet<ApprovalOutbox> ApprovalOutbox => Set<ApprovalOutbox>();
    public DbSet<DecisionGrant> DecisionGrants => Set<DecisionGrant>();

    /// <summary>Approval audit entries are append-only: an update or delete through the context is a programming error.</summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void GuardAppendOnly()
    {
        if (ChangeTracker.Entries<ApprovalAudit>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Approval audit entries are immutable.");
    }

    /// <summary>SQLite cannot compare DateTimeOffset text; store every timestamp as UTC ticks (INTEGER) instead.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.DisplayName).HasMaxLength(80);
        });

        b.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(80);
        });

        b.Entity<Membership>(e =>
        {
            e.HasKey(x => new { x.TenantId, x.AccountId });
            e.HasOne(x => x.Tenant).WithMany(t => t.Memberships).HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Account).WithMany(a => a.Memberships).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Role).HasMaxLength(16);
        });

        b.Entity<Credential>(e =>
        {
            e.HasIndex(x => x.Hash).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Kind });
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Label).HasMaxLength(80);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.Hint).HasMaxLength(32);
            e.Property(x => x.Scopes).HasMaxLength(200);
            e.Property(x => x.PublisherType).HasMaxLength(16);
            e.HasIndex(x => x.ServiceAccountId);
            e.Ignore(x => x.ScopeList);
        });

        b.Entity<ServiceAccount>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.Scopes).HasMaxLength(200);
            e.Property(x => x.AllowedTags).HasMaxLength(700);
            e.Property(x => x.AllowedUrlHosts).HasMaxLength(700);
            e.Property(x => x.PublisherType).HasMaxLength(16);
            e.Ignore(x => x.ScopeList);
            e.Ignore(x => x.AllowedTagList);
            e.Ignore(x => x.AllowedUrlHostList);
        });

        b.Entity<OAuthClient>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.RedirectUris).HasMaxLength(4000);
            e.Property(x => x.AuthMethod).HasMaxLength(32);
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.Property(x => x.ClientUri).HasMaxLength(2048);
            e.Property(x => x.LogoUri).HasMaxLength(2048);
            e.Ignore(x => x.RedirectUriList);
        });

        b.Entity<OAuthGrant>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.AccountId, x.ClientId });
            e.HasIndex(x => x.ServiceAccountId);
            e.Property(x => x.ClientId).HasMaxLength(64);
            e.Property(x => x.Scopes).HasMaxLength(200);
        });

        b.Entity<OAuthCode>(e =>
        {
            e.HasIndex(x => x.CodeHash).IsUnique();
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.Property(x => x.ClientId).HasMaxLength(64);
            e.Property(x => x.RedirectUri).HasMaxLength(2048);
            e.Property(x => x.CodeChallenge).HasMaxLength(128);
            e.Property(x => x.Resource).HasMaxLength(2048);
            e.Property(x => x.Scopes).HasMaxLength(200);
        });

        b.Entity<OAuthRefreshToken>(e =>
        {
            e.HasIndex(x => x.Hash).IsUnique();
            e.HasIndex(x => x.GrantId);
            e.Property(x => x.Hash).HasMaxLength(64);
        });

        b.Entity<PasswordReset>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.AccountId, x.CreatedAt });
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.RequestIp).HasMaxLength(64);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.At });
            e.HasIndex(x => new { x.ServiceAccountId, x.At });
            e.Property(x => x.Action).HasMaxLength(40);
            e.Property(x => x.Target).HasMaxLength(200);
            e.Property(x => x.Detail).HasMaxLength(500);
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.Ip).HasMaxLength(64);
        });

        b.Entity<Endpoint>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.EndpointId }).IsUnique();
            e.Property(x => x.EndpointId).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Platform).HasMaxLength(32);
            e.Property(x => x.AgentVersion).HasMaxLength(32);
            e.Property(x => x.PushProvider).HasMaxLength(16);
            e.Property(x => x.PushToken).HasMaxLength(Contracts.PushProviders.TokenMax);
            e.Ignore(x => x.CanPush);
        });

        b.Entity<Subscription>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.EndpointRef, x.Tag }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Tag });
            e.Property(x => x.Tag).HasMaxLength(64);
            e.HasOne(x => x.Endpoint).WithMany(x => x.Subscriptions).HasForeignKey(x => x.EndpointRef).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.PublishedAt });
            e.Property(x => x.TagsCsv).HasMaxLength(700);
            e.Property(x => x.Title).HasMaxLength(120);
            e.Property(x => x.Body).HasMaxLength(1000);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.Priority).HasMaxLength(8);
            e.Property(x => x.PayloadHash).HasMaxLength(64);
            e.Property(x => x.PublisherName).HasMaxLength(80);
            e.Property(x => x.PublisherType).HasMaxLength(16);
            e.Property(x => x.Channel).HasMaxLength(16);
            e.Ignore(x => x.Tags);
        });

        b.Entity<Delivery>(e =>
        {
            e.HasIndex(x => new { x.NotificationId, x.EndpointRef }).IsUnique();
            e.HasIndex(x => new { x.EndpointRef, x.Status });
            e.Property(x => x.Status).HasMaxLength(8);
            e.HasOne(x => x.Notification).WithMany(x => x.Deliveries).HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Endpoint).WithMany().HasForeignKey(x => x.EndpointRef).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Approval>(e =>
        {
            // Idempotent creation: one approval per (workspace, requesting credential, Idempotency-Key).
            e.HasIndex(x => new { x.TenantId, x.RequesterCredentialId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status, x.ExpiresAt });
            e.HasIndex(x => x.NotificationId).IsUnique();
            e.Property(x => x.RequesterName).HasMaxLength(80);
            e.Property(x => x.RequesterType).HasMaxLength(16);
            e.Property(x => x.IdempotencyKey).HasMaxLength(Contracts.ApprovalLimits.IdempotencyKeyMax);
            e.Property(x => x.PayloadHash).HasMaxLength(64);
            e.Property(x => x.RequesterCorrelationId).HasMaxLength(Contracts.ApprovalLimits.CorrelationIdMax);
            e.Property(x => x.Subject).HasMaxLength(Contracts.ApprovalLimits.SubjectMax);
            e.Property(x => x.Description).HasMaxLength(Contracts.ApprovalLimits.DescriptionMax);
            e.Property(x => x.ApproverIdsCsv).HasMaxLength(400);
            e.Property(x => x.TagsCsv).HasMaxLength(700);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.ResolutionCause).HasMaxLength(16);
            e.Property(x => x.DecidedByName).HasMaxLength(80);
            e.Property(x => x.DecisionNote).HasMaxLength(Contracts.ApprovalLimits.NoteMax);
            e.Property(x => x.DecisionIdempotencyKey).HasMaxLength(Contracts.ApprovalLimits.IdempotencyKeyMax);
            e.Property(x => x.DetailUrl).HasMaxLength(2048);
            e.Ignore(x => x.ApproverIds);
            e.Ignore(x => x.Tags);
        });

        b.Entity<ApprovalAudit>(e =>
        {
            e.HasIndex(x => new { x.ApprovalId, x.At });
            e.HasIndex(x => new { x.TenantId, x.At });
            e.Property(x => x.Action).HasMaxLength(32);
            e.Property(x => x.ActorKind).HasMaxLength(16);
            e.Property(x => x.CorrelationId).HasMaxLength(Contracts.ApprovalLimits.CorrelationIdMax);
            e.Property(x => x.RequestId).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(300);
        });

        b.Entity<ApprovalOutbox>(e =>
        {
            // A transition enqueues its event once.
            e.HasIndex(x => new { x.ApprovalId, x.Kind, x.Version }).IsUnique();
            e.HasIndex(x => new { x.ProcessedAt, x.NextAttemptAt });
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Payload).HasMaxLength(2000);
            e.Property(x => x.LastError).HasMaxLength(500);
        });

        b.Entity<DecisionGrant>(e =>
        {
            e.HasIndex(x => x.PollCodeHash).IsUnique();
            e.HasIndex(x => x.SecretHash).IsUnique();
            e.HasIndex(x => x.UserCode);
            e.HasIndex(x => new { x.AccountId, x.Status });
            e.HasIndex(x => x.DeviceCredentialId);
            e.Property(x => x.DeviceName).HasMaxLength(80);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.UserCode).HasMaxLength(16);
            e.Property(x => x.PollCodeHash).HasMaxLength(64);
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.Property(x => x.IssuedSecret).HasMaxLength(64);
        });

        b.Entity<DevicePairing>(e =>
        {
            e.HasIndex(x => x.DeviceCodeHash).IsUnique();
            e.HasIndex(x => x.UserCode);
            e.Property(x => x.UserCode).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.EndpointId).HasMaxLength(64);
            e.Property(x => x.DeviceName).HasMaxLength(80);
            e.Property(x => x.Platform).HasMaxLength(32);
            e.Property(x => x.Client).HasMaxLength(16);
        });
    }
}

public sealed class UtcTicksConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
    v => v.UtcTicks,
    v => new DateTimeOffset(v, TimeSpan.Zero));
