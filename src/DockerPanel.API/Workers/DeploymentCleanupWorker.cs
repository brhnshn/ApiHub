using DockerPanel.Domain.Interfaces;

namespace DockerPanel.API.Workers;

public sealed class DeploymentCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeploymentCleanupWorker> _logger;

    public DeploymentCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<DeploymentCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var deployments = scope.ServiceProvider.GetRequiredService<IDeploymentService>();
                var expired = await deployments.GetExpiredRollbackCandidatesAsync(DateTimeOffset.UtcNow, stoppingToken);
                foreach (var deployment in expired)
                {
                    try { await deployments.RollbackAsync(deployment.Id, stoppingToken); }
                    catch (Exception ex) { _logger.LogError(ex, "Automatic rollback failed for deployment {DeploymentId}", deployment.Id); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { _logger.LogError(ex, "Deployment cleanup worker failed"); }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
