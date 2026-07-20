using DockerPanel.Domain.Enums;

namespace DockerPanel.Domain.Entities;

public class Deployment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? ProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public DeploymentSourceType SourceType { get; set; }
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;
    public string? SourceReference { get; set; }
    public string? CommitSha { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? RequestJson { get; set; }
    public string? ProxyService { get; set; }
    public string? DomainName { get; set; }
    public string? SubdomainName { get; set; }
    public int? HostPort { get; set; }
    public int? ContainerPort { get; set; }
    public bool SslEnabled { get; set; }
    public string? RollbackClaim { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? RollbackExpiresAt { get; set; }
    public virtual User User { get; set; } = null!;
    public virtual Project? Project { get; set; }
    public virtual ICollection<DeploymentStep> Steps { get; set; } = new List<DeploymentStep>();
    public virtual ICollection<DeploymentResource> Resources { get; set; } = new List<DeploymentResource>();
}
