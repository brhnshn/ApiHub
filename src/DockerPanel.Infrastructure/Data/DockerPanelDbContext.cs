using Microsoft.EntityFrameworkCore;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;

namespace DockerPanel.Infrastructure.Data;

public class DockerPanelDbContext : DbContext
{
    public DockerPanelDbContext(DbContextOptions<DockerPanelDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Subdomain> Subdomains => Set<Subdomain>();
    public DbSet<DnsRecord> DnsRecords => Set<DnsRecord>();
    public DbSet<DatabaseSchema> DatabaseSchemas => Set<DatabaseSchema>();
    public DbSet<MailAccount> MailAccounts => Set<MailAccount>();
    public DbSet<RootDomain> RootDomains => Set<RootDomain>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<PushNotification> PushNotifications => Set<PushNotification>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. User Entity Configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Username)
                .HasMaxLength(50)
                .IsRequired();
            
            entity.HasIndex(e => e.Username)
                .IsUnique();

            entity.Property(e => e.PasswordHash)
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(e => e.Role)
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // 2. Project Entity Configuration
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("Projects");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.DockerContainerId)
                .HasMaxLength(128);

            entity.Property(e => e.Name)
                .HasMaxLength(64)
                .IsRequired();

            entity.HasIndex(e => e.Name)
                .IsUnique();

            entity.Property(e => e.Type)
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.ImageOrPath)
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(e => e.MemoryLimitBytes)
                .IsRequired();

            entity.Property(e => e.CpuCount)
                .IsRequired();

            entity.Property(e => e.InternalPort)
                .IsRequired();

            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.StartedAt);

            entity.Property(e => e.EnablePhp)
                .HasDefaultValue(false)
                .IsRequired();

            // Foreign Key: User -> Projects (Cascade Delete)
            entity.HasOne(e => e.User)
                .WithMany(u => u.Projects)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 3. Subdomain Entity Configuration
        modelBuilder.Entity<Subdomain>(entity =>
        {
            entity.ToTable("Subdomains");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.SubdomainName)
                .HasMaxLength(63)
                .IsRequired();

            entity.Property(e => e.DomainName)
                .HasMaxLength(253)
                .IsRequired();

            entity.Property(e => e.SslEnabled)
                .HasDefaultValue(true)
                .IsRequired();

            // Unique index for SubdomainName and DomainName
            entity.HasIndex(e => new { e.SubdomainName, e.DomainName })
                .IsUnique();

            // Foreign Key: User -> Subdomains (Cascade Delete)
            entity.HasOne(e => e.User)
                .WithMany(u => u.Subdomains)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign Key: Project -> Subdomains (Cascade Delete)
            entity.HasOne(e => e.Project)
                .WithMany(p => p.Subdomains)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);
        });

        // 4. DnsRecord Entity Configuration
        modelBuilder.Entity<DnsRecord>(entity =>
        {
            entity.ToTable("DnsRecords");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Type)
                .HasMaxLength(10)
                .IsRequired();

            entity.Property(e => e.Name)
                .HasMaxLength(253)
                .IsRequired();

            entity.Property(e => e.Value)
                .IsRequired();

            entity.Property(e => e.Ttl)
                .HasDefaultValue(3600)
                .IsRequired();

            entity.Property(e => e.Proxied)
                .HasDefaultValue(false)
                .IsRequired();

            entity.Property(e => e.CloudflareRecordId)
                .HasMaxLength(128);

            // Foreign Key: User -> DnsRecords (Cascade Delete)
            entity.HasOne(e => e.User)
                .WithMany(u => u.DnsRecords)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.DnsRecords)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });

        // 5. DatabaseSchema Entity Configuration
        modelBuilder.Entity<DatabaseSchema>(entity =>
        {
            entity.ToTable("DatabaseSchemas");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.DbName)
                .HasMaxLength(63)
                .IsRequired();

            entity.HasIndex(e => e.DbName)
                .IsUnique();

            entity.Property(e => e.DbUser)
                .HasMaxLength(63)
                .IsRequired();

            entity.HasIndex(e => e.DbUser)
                .IsUnique();

            // Foreign Key: User -> DatabaseSchemas (Cascade Delete)
            entity.HasOne(e => e.User)
                .WithMany(u => u.DatabaseSchemas)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.DatabaseSchemas)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });

        // 6. MailAccount Entity Configuration
        modelBuilder.Entity<MailAccount>(entity =>
        {
            entity.ToTable("MailAccounts");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.EmailAddress)
                .HasMaxLength(254)
                .IsRequired();

            entity.HasIndex(e => e.EmailAddress)
                .IsUnique();

            entity.Property(e => e.QuotaBytes)
                .IsRequired();

            // Foreign Key: User -> MailAccounts (Cascade Delete)
            entity.HasOne(e => e.User)
                .WithMany(u => u.MailAccounts)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 7. RootDomain Entity Configuration
        modelBuilder.Entity<RootDomain>(entity =>
        {
            entity.ToTable("RootDomains");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name)
                .HasMaxLength(253)
                .IsRequired();

            entity.HasIndex(e => e.Name)
                .IsUnique();

            entity.Property(e => e.CloudflareToken)
                .HasMaxLength(255);

            entity.Property(e => e.CloudflareZoneId)
                .HasMaxLength(128);

            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Foreign Key: User -> RootDomains (Cascade Delete)
            entity.HasOne(e => e.User)
                .WithMany(u => u.RootDomains)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 8. AuditLog Entity Configuration
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLogs");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Action)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.TargetEntity)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.TargetId);

            entity.Property(e => e.Details)
                .IsRequired();

            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .IsRequired();

            entity.Property(e => e.UserAgent)
                .HasMaxLength(512)
                .IsRequired();

            entity.Property(e => e.Timestamp)
                .IsRequired();

            // Foreign Key: User -> AuditLogs (Cascade Delete)
            entity.HasOne(e => e.User)
                .WithMany(u => u.AuditLogs)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 9. DeviceToken Entity Configuration
        modelBuilder.Entity<DeviceToken>(entity =>
        {
            entity.ToTable("DeviceTokens");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Token)
                .HasMaxLength(512)
                .IsRequired();

            entity.HasIndex(e => e.Token)
                .IsUnique();

            entity.Property(e => e.Platform)
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.DeviceName)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .IsRequired();

            entity.Property(e => e.LastUsedAt)
                .IsRequired();

            // Foreign Key: User -> DeviceTokens (Cascade Delete)
            entity.HasOne(e => e.User)
                .WithMany(u => u.DeviceTokens)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 10. PushNotification Entity Configuration
        modelBuilder.Entity<PushNotification>(entity =>
        {
            entity.ToTable("PushNotifications");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Title)
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(e => e.Body)
                .IsRequired();

            entity.Property(e => e.DeepLink)
                .HasMaxLength(512);

            entity.Property(e => e.SentAt)
                .IsRequired();

            entity.Property(e => e.IsRead)
                .HasDefaultValue(false)
                .IsRequired();

            // Foreign Key: User -> PushNotifications (Cascade Delete)
            entity.HasOne(e => e.User)
                .WithMany(u => u.PushNotifications)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 11. ApiKey Entity Configuration
        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("ApiKeys");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(e => e.KeyHash)
                .HasMaxLength(256)
                .IsRequired();

            entity.HasIndex(e => e.KeyHash)
                .IsUnique();

            entity.Property(e => e.MaskedKey)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .IsRequired();

            entity.Property(e => e.LastUsedAt);
            entity.Property(e => e.ExpiresAt);

            // Foreign Key: User -> ApiKeys (Cascade Delete)
            entity.HasOne(e => e.User)
                .WithMany(u => u.ApiKeys)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
