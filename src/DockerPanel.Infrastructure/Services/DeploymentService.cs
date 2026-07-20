using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DockerPanel.Infrastructure.Services;

public sealed class DeploymentService : IDeploymentService
{
    private readonly DockerPanelDbContext _db;
    private readonly IProjectContainerService _containers;
    private readonly INginxService _nginx;
    private readonly ILogger<DeploymentService> _logger;

    public DeploymentService(DockerPanelDbContext db, IProjectContainerService containers, INginxService nginx, ILogger<DeploymentService> logger)
    {
        _db = db;
        _containers = containers;
        _nginx = nginx;
        _logger = logger;
    }

    public async Task<Deployment> CreateAsync(Guid userId, string projectName, DeploymentSourceType sourceType, Guid? projectId = null, string? sourceReference = null, CancellationToken cancellationToken = default)
    {
        var deployment = new Deployment { UserId = userId, ProjectId = projectId, ProjectName = projectName, SourceType = sourceType, SourceReference = sourceReference, Status = DeploymentStatus.Queued };
        _db.Deployments.Add(deployment);
        var names = sourceType == DeploymentSourceType.DockerCompose
            ? new[] { "ProjectDirectory", "ComposeConfig", "Environment", "ComposePull", "ComposeUp", "HealthCheck", "ReverseProxy", "Ssl" }
            : sourceType == DeploymentSourceType.GitHubRepository
                ? new[] { "CloneRepository", "ResolveCommit", "SourceBuild", "HealthCheck", "ReverseProxy", "Ssl" }
                : new[] { "ImagePull", "ContainerCreate", "HealthCheck", "ReverseProxy", "Ssl" };
        for (var i = 0; i < names.Length; i++) deployment.Steps.Add(new DeploymentStep { DeploymentId = deployment.Id, Name = names[i], Order = i + 1 });
        await _db.SaveChangesAsync(cancellationToken);
        return deployment;
    }

    public async Task AddResourceAsync(Guid deploymentId, string resourceType, string resourceKey, string resourceValue, bool preserveOnRollback = false, CancellationToken cancellationToken = default)
    {
        if (await _db.DeploymentResources.AnyAsync(r => r.DeploymentId == deploymentId && r.ResourceType == resourceType && r.ResourceKey == resourceKey, cancellationToken)) return;
        _db.DeploymentResources.Add(new DeploymentResource { DeploymentId = deploymentId, ResourceType = resourceType, ResourceKey = resourceKey, ResourceValue = resourceValue, PreserveOnRollback = preserveOnRollback });
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStatusAsync(Guid deploymentId, DeploymentStatus status, string? error = null, CancellationToken cancellationToken = default)
    {
        var deployment = await _db.Deployments.FindAsync(new object[] { deploymentId }, cancellationToken) ?? throw new KeyNotFoundException("Deployment bulunamadı.");
        deployment.Status = status;
        deployment.ErrorMessage = error;
        if (status == DeploymentStatus.RollbackPending) deployment.RollbackExpiresAt = DateTimeOffset.UtcNow.AddSeconds(10);
        if (status is DeploymentStatus.Succeeded or DeploymentStatus.RolledBack or DeploymentStatus.RollbackFailed) deployment.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetRequestAsync(Guid deploymentId, string requestJson, CancellationToken cancellationToken = default)
    {
        var deployment = await _db.Deployments.FindAsync(new object[] { deploymentId }, cancellationToken) ?? throw new KeyNotFoundException("Deployment bulunamadı.");
        deployment.RequestJson = requestJson;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetWorkingDirectoryAsync(Guid deploymentId, string path, CancellationToken cancellationToken = default)
    {
        var deployment = await _db.Deployments.FindAsync(new object[] { deploymentId }, cancellationToken) ?? throw new KeyNotFoundException("Deployment bulunamadı.");
        deployment.WorkingDirectory = path;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetProjectIdAsync(Guid deploymentId, Guid projectId, CancellationToken cancellationToken = default)
    {
        var deployment = await _db.Deployments.FindAsync(new object[] { deploymentId }, cancellationToken) ?? throw new KeyNotFoundException("Deployment bulunamadı.");
        deployment.ProjectId = projectId;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryClaimRollbackAsync(Guid deploymentId, string claim, CancellationToken cancellationToken = default)
    {
        var changed = await _db.Deployments
            .Where(d => d.Id == deploymentId && d.Status == DeploymentStatus.RollbackPending && d.RollbackClaim == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(d => d.Status, DeploymentStatus.RollingBack)
                .SetProperty(d => d.RollbackClaim, claim), cancellationToken);
        return changed == 1;
    }

    public async Task SetStepAsync(Guid deploymentId, string stepName, DeploymentStepStatus status, string? error = null, CancellationToken cancellationToken = default)
    {
        var step = await _db.DeploymentSteps.FirstOrDefaultAsync(s => s.DeploymentId == deploymentId && s.Name == stepName, cancellationToken);
        if (step == null) return;
        step.Status = status;
        step.ErrorMessage = error;
        if (status == DeploymentStepStatus.Running) step.StartedAt = DateTimeOffset.UtcNow;
        if (status is DeploymentStepStatus.Succeeded or DeploymentStepStatus.Failed or DeploymentStepStatus.RolledBack) step.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<Deployment?> GetAsync(Guid deploymentId, CancellationToken cancellationToken = default) =>
        _db.Deployments.Include(d => d.Steps).Include(d => d.Resources).FirstOrDefaultAsync(d => d.Id == deploymentId, cancellationToken);

    public async Task<IReadOnlyList<Deployment>> GetExpiredRollbackCandidatesAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        await _db.Deployments.Where(d => d.Status == DeploymentStatus.RollbackPending && d.RollbackExpiresAt <= now).ToListAsync(cancellationToken);

    public async Task RollbackAsync(Guid deploymentId, CancellationToken cancellationToken = default)
    {
        var deployment = await GetAsync(deploymentId, cancellationToken);
        if (deployment == null) throw new KeyNotFoundException("Deployment bulunamadı.");
        if (deployment.Status == DeploymentStatus.RolledBack) return;
        if (deployment.Status == DeploymentStatus.RollbackFailed)
        {
            deployment.Status = DeploymentStatus.RollbackPending;
            deployment.RollbackClaim = null;
            deployment.RollbackExpiresAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
        if (deployment.Status != DeploymentStatus.RollbackPending || !await TryClaimRollbackAsync(deploymentId, $"rollback-{Guid.NewGuid():N}", cancellationToken)) return;
        deployment = await GetAsync(deploymentId, cancellationToken) ?? throw new KeyNotFoundException("Deployment bulunamadı.");
        var failures = new List<string>();

        var rollbackOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["compose-project"] = 0,
            ["nginx"] = 1,
            ["container"] = 2,
            ["file"] = 3,
            ["directory"] = 4
        };
        foreach (var resource in deployment.Resources.Where(r => !r.IsRemoved && !r.PreserveOnRollback)
                     .OrderBy(r => rollbackOrder.TryGetValue(r.ResourceType, out var order) ? order : 5))
        {
            try
            {
                switch (resource.ResourceType)
                {
                    case "container":
                        await _containers.DeleteContainerAsync(resource.ResourceValue);
                        break;
                    case "image":
                        await _containers.DeleteImageAsync(resource.ResourceValue);
                        break;
                    case "nginx":
                        var parts = resource.ResourceKey.Split('|', 2);
                        if (parts.Length == 2) await _nginx.DeleteSubdomainAsync(parts[0], parts[1]);
                        break;
                    case "directory":
                        if (Directory.Exists(resource.ResourceValue)) Directory.Delete(resource.ResourceValue, true);
                        break;
                    case "compose-project":
                        await ComposeDeploymentService.RunAsync(
                            deployment.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                            "docker",
                            new[] { "compose", "-p", resource.ResourceValue, "down", "--remove-orphans" },
                            cancellationToken);
                        break;
                    case "file":
                        if (File.Exists(resource.ResourceValue)) File.Delete(resource.ResourceValue);
                        break;
                }
                resource.IsRemoved = true;
            }
            catch (Exception ex)
            {
                failures.Add($"{resource.ResourceType}:{resource.ResourceKey} - {ex.Message}");
                _logger.LogWarning(ex, "Deployment resource rollback failed: {Resource}", resource.ResourceKey);
            }
        }

        if (failures.Count > 0)
        {
            deployment.Status = DeploymentStatus.RollbackFailed;
            deployment.ErrorMessage = string.Join(" | ", failures);
        }
        else
        {
            deployment.Status = DeploymentStatus.RolledBack;
            deployment.ErrorMessage = null;
        }
        deployment.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
