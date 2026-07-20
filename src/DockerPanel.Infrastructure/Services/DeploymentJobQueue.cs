using System.Threading.Channels;
using DockerPanel.Domain.Interfaces;

namespace DockerPanel.Infrastructure.Services;

public sealed class DeploymentJobQueue : IDeploymentJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    public ValueTask EnqueueAsync(Guid deploymentId, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(deploymentId, cancellationToken);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken = default) => _channel.Reader.ReadAllAsync(cancellationToken);
}
