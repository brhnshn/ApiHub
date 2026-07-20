namespace DockerPanel.Domain.Interfaces;

public interface IDeploymentJobQueue
{
    ValueTask EnqueueAsync(Guid deploymentId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken = default);
}
