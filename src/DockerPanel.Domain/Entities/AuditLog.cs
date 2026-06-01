using System;

namespace DockerPanel.Domain.Entities;

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetEntity { get; set; } = string.Empty;
    public Guid? TargetId { get; set; }
    public string Details { get; set; } = string.Empty; // Holds JSON details as a string
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    // Navigation Property
    public virtual User User { get; set; } = null!;
}
