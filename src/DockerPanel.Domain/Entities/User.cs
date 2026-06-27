using System;
using System.Collections.Generic;
using DockerPanel.Domain.Enums;

namespace DockerPanel.Domain.Entities;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Customer;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation Properties
    public virtual ICollection<Project> Projects { get; set; } = new List<Project>();
    public virtual ICollection<Subdomain> Subdomains { get; set; } = new List<Subdomain>();
    public virtual ICollection<DnsRecord> DnsRecords { get; set; } = new List<DnsRecord>();
    public virtual ICollection<DatabaseSchema> DatabaseSchemas { get; set; } = new List<DatabaseSchema>();
    public virtual ICollection<MailAccount> MailAccounts { get; set; } = new List<MailAccount>();
    public virtual ICollection<RootDomain> RootDomains { get; set; } = new List<RootDomain>();
    public virtual ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
    public virtual ICollection<DeviceToken> DeviceTokens { get; set; } = new List<DeviceToken>();
    public virtual ICollection<PushNotification> PushNotifications { get; set; } = new List<PushNotification>();
    public virtual ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
}
