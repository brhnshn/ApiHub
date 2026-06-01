using System;

namespace DockerPanel.Domain.Entities;

public class DatabaseSchema
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? ProjectId { get; set; }
    public string DbName { get; set; } = string.Empty;
    public string DbUser { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation Properties
    public virtual User User { get; set; } = null!;
    public virtual Project? Project { get; set; }
}
