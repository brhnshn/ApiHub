using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;

namespace DockerPanel.Domain.Interfaces;

public interface IDeploymentService
{
    Task<Deployment> CreateAsync(Guid userId, string projectName, DeploymentSourceType sourceType, Guid? projectId = null, string? sourceReference = null, CancellationToken cancellationToken = default);
    Task AddResourceAsync(Guid deploymentId, string resourceType, string resourceKey, string resourceValue, bool preserveOnRollback = false, CancellationToken cancellationToken = default);
    Task SetStatusAsync(Guid deploymentId, DeploymentStatus status, string? error = null, CancellationToken cancellationToken = default);
    Task SetRequestAsync(Guid deploymentId, string requestJson, CancellationToken cancellationToken = default);
    Task SetWorkingDirectoryAsync(Guid deploymentId, string path, CancellationToken cancellationToken = default);
    Task SetProjectIdAsync(Guid deploymentId, Guid projectId, CancellationToken cancellationToken = default);
    Task<bool> TryClaimRollbackAsync(Guid deploymentId, string claim, CancellationToken cancellationToken = default);
    Task SetStepAsync(Guid deploymentId, string stepName, DeploymentStepStatus status, string? error = null, CancellationToken cancellationToken = default);
    Task<Deployment?> GetAsync(Guid deploymentId, CancellationToken cancellationToken = default);
    Task RollbackAsync(Guid deploymentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Deployment>> GetExpiredRollbackCandidatesAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}
