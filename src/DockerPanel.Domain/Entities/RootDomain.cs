using System;

namespace DockerPanel.Domain.Entities;

public class RootDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    
    public string Name { get; set; } = string.Empty; 
    
    public string? CloudflareToken { get; set; }
    public string? CloudflareZoneId { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // EF Core Relationships
    public virtual User User { get; set; } = null!;
}
