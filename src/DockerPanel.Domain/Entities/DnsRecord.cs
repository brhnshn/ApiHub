using System;

namespace DockerPanel.Domain.Entities;

public class DnsRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? ProjectId { get; set; }
    public string Type { get; set; } = "A";
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int Ttl { get; set; } = 3600;
    public bool Proxied { get; set; } = false;
    public string? CloudflareRecordId { get; set; }

    // Navigation Properties
    public virtual User User { get; set; } = null!;
    public virtual Project? Project { get; set; }
}
