using DockerPanel.Domain.Enums;

namespace DockerPanel.Domain.Entities;

public class DeploymentStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeploymentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public DeploymentStepStatus Status { get; set; } = DeploymentStepStatus.Pending;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public virtual Deployment Deployment { get; set; } = null!;
}
