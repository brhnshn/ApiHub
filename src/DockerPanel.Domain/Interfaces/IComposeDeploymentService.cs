namespace DockerPanel.Domain.Interfaces;

public interface IComposeDeploymentService
{
    Task DeployAsync(Guid deploymentId, string projectName, string composeContent, IReadOnlyDictionary<string, string> environment, string? proxyService, CancellationToken cancellationToken = default);
}
