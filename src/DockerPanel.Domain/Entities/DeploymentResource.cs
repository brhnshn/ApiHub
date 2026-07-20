namespace DockerPanel.Domain.Entities;

public class DeploymentResource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeploymentId { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceKey { get; set; } = string.Empty;
    public string ResourceValue { get; set; } = string.Empty;
    public bool PreserveOnRollback { get; set; }
    public bool IsRemoved { get; set; }
    public virtual Deployment Deployment { get; set; } = null!;
}
