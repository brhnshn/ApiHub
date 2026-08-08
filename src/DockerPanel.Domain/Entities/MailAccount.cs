using System;

namespace DockerPanel.Domain.Entities;

public class MailAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long QuotaBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    
    public string? ForwardingAddress { get; set; }
    public bool ForwardingEnabled { get; set; } = false;

    // Navigation Properties
    public virtual User User { get; set; } = null!;
}
