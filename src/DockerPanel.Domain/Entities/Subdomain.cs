using System;

namespace DockerPanel.Domain.Entities;

public class Subdomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? ProjectId { get; set; }
    public string SubdomainName { get; set; } = string.Empty;
    public string DomainName { get; set; } = string.Empty;
    public bool SslEnabled { get; set; } = true;
    public Guid? ActiveMaintenancePageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation Properties
    public virtual User User { get; set; } = null!;
    public virtual Project? Project { get; set; }
}
