namespace DockerPanel.Domain.Interfaces;

public interface IGitHubDeploymentService
{
    Task DeployAsync(Guid deploymentId, string projectName, Uri repository, string? reference, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken = default);
}
