using System;

namespace DockerPanel.Domain.Entities;

public class ApiKeyProjectPermission
{
    public Guid ApiKeyId { get; set; }
    public Guid ProjectId { get; set; }

    // Navigation Properties
    public virtual ApiKey ApiKey { get; set; } = null!;
    public virtual Project Project { get; set; } = null!;
}
