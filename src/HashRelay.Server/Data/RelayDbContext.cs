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
            e.Property(x => x.Scopes).HasMaxLength(64);
            e.Ignore(x => x.ScopeList);
        });

        b.Entity<Endpoint>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.EndpointId }).IsUnique();
            e.Property(x => x.EndpointId).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Platform).HasMaxLength(32);
            e.Property(x => x.AgentVersion).HasMaxLength(32);
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
