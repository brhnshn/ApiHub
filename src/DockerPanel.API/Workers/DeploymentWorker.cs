using System.Text.Json;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Entities;
using DockerPanel.Infrastructure.Services;
using DockerPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DockerPanel.API.Workers;

public sealed class DeploymentWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeploymentJobQueue _queue;
    private readonly ILogger<DeploymentWorker> _logger;

    public DeploymentWorker(IServiceScopeFactory scopeFactory, IDeploymentJobQueue queue, ILogger<DeploymentWorker> logger)
    { _scopeFactory = scopeFactory; _queue = queue; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await ExecuteOneAsync(scope.ServiceProvider, id, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deployment worker failed for {DeploymentId}", id);
                using var scope = _scopeFactory.CreateScope();
                var deployments = scope.ServiceProvider.GetRequiredService<IDeploymentService>();
                try { await deployments.SetStatusAsync(id, DeploymentStatus.RollbackPending, "Deployment pipeline başarısız oldu.", stoppingToken); } catch { }
            }
        }
    }

    private static async Task ExecuteOneAsync(IServiceProvider services, Guid id, CancellationToken ct)
    {
        var deployments = services.GetRequiredService<IDeploymentService>();
        var deployment = await deployments.GetAsync(id, ct) ?? throw new InvalidOperationException("Deployment bulunamadı.");
        var nginx = services.GetRequiredService<INginxService>();
        var db = services.GetRequiredService<DockerPanelDbContext>();
        await deployments.SetStatusAsync(id, DeploymentStatus.Provisioning, cancellationToken: ct);
        await deployments.SetStepAsync(id, deployment.SourceType == DeploymentSourceType.DockerCompose ? "ProjectDirectory" : "Provisioning", DeploymentStepStatus.Running, cancellationToken: ct);
        switch (deployment.SourceType)
        {
            case DeploymentSourceType.DockerCompose:
                var compose = services.GetRequiredService<IComposeDeploymentService>();
                var composeRequest = JsonSerializer.Deserialize<ComposeJobRequest>(deployment.RequestJson ?? "{}") ?? throw new InvalidOperationException("Compose isteği bulunamadı.");
                await compose.DeployAsync(id, deployment.ProjectName, composeRequest.ComposeContent, composeRequest.Environment ?? new Dictionary<string, string>(), composeRequest.ProxyService, ct);
                await WaitForComposeAsync(deployment.WorkingDirectory!, deployment.ProjectName, id, ct);
                if (!string.IsNullOrWhiteSpace(composeRequest.DomainName) && composeRequest.ProxyPort.HasValue)
                    await ConfigureProxyAsync(nginx, composeRequest.SubdomainName, composeRequest.DomainName, $"{deployment.ProjectName}-deploy-{id:N}", composeRequest.ProxyPort.Value, composeRequest.SslEnabled, ct);
                await EnsureProjectAsync(db, deployments, deployment, composeRequest.ProxyPort, deployment.WorkingDirectory, null, ct);
                break;
            case DeploymentSourceType.GitHubRepository:
                var github = services.GetRequiredService<IGitHubDeploymentService>();
                var githubRequest = JsonSerializer.Deserialize<GitHubJobRequest>(deployment.RequestJson ?? "{}") ?? throw new InvalidOperationException("GitHub isteği bulunamadı.");
                await github.DeployAsync(id, deployment.ProjectName, new Uri(githubRequest.Repository), githubRequest.Reference, githubRequest.Environment ?? new Dictionary<string, string>(), ct);
                var hasCompose = (await deployments.GetAsync(id, ct))!.Resources.Any(r => r.ResourceType == "compose-project");
                if (hasCompose) await WaitForComposeAsync(deployment.WorkingDirectory!, deployment.ProjectName, id, ct);
                if (!hasCompose && githubRequest.ProxyPort.HasValue && !string.IsNullOrWhiteSpace(githubRequest.DomainName))
                    await ConfigureProxyAsync(nginx, githubRequest.SubdomainName, githubRequest.DomainName, $"{deployment.ProjectName}-deploy-{id:N}", githubRequest.ProxyPort.Value, githubRequest.SslEnabled, ct);
                await EnsureProjectAsync(db, deployments, deployment, githubRequest.ProxyPort, deployment.WorkingDirectory, null, ct);
                break;
            case DeploymentSourceType.DockerImage:
                var container = services.GetRequiredService<IProjectContainerService>();
                var containerRequest = JsonSerializer.Deserialize<ContainerJobRequest>(deployment.RequestJson ?? "{}") ?? throw new InvalidOperationException("Container isteği bulunamadı.");
                var name = $"{deployment.ProjectName}-deploy-{id:N}";
                var port = containerRequest.ContainerPort ?? await container.GetImageExposedPortAsync(containerRequest.ImageName) ?? containerRequest.HostPort;
                var containerId = await container.ProvisionContainerAsync(name, containerRequest.ImageName, containerRequest.MemoryLimitBytes, containerRequest.CpuCount, containerRequest.HostPort, port);
                await deployments.AddResourceAsync(id, "container", name, containerId, cancellationToken: ct);
                if (!await container.WaitForContainerHealthAsync(containerId, TimeSpan.FromSeconds(60), ct)) throw new InvalidOperationException("Container health check başarısız oldu.");
                if (!string.IsNullOrWhiteSpace(containerRequest.DomainName))
                    await ConfigureProxyAsync(nginx, containerRequest.SubdomainName, containerRequest.DomainName, name, containerRequest.HostPort, containerRequest.SslEnabled, ct);
                await EnsureProjectAsync(db, deployments, deployment, containerRequest.HostPort, containerRequest.ImageName, containerId, ct);
                break;
        }
        await deployments.SetStatusAsync(id, DeploymentStatus.SslConfiguring, cancellationToken: ct);
        await deployments.SetStatusAsync(id, DeploymentStatus.Succeeded, cancellationToken: ct);
    }

    private static async Task ConfigureProxyAsync(INginxService nginx, string? subdomain, string domain, string target, int port, bool ssl, CancellationToken ct)
    {
        await nginx.ProvisionSubdomainAsync(subdomain ?? "@", domain, target, port, reloadNginx: true);
        if (ssl) await nginx.EnableSslWithCertbotAsync(subdomain ?? "@", domain);
    }

    private static async Task EnsureProjectAsync(DockerPanelDbContext db, IDeploymentService deployments, DockerPanel.Domain.Entities.Deployment deployment, int? hostPort, string? imageOrPath, string? containerId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.UserId == deployment.UserId && p.Name == deployment.ProjectName, ct);
        if (project == null)
        {
            project = new DockerPanel.Domain.Entities.Project { UserId = deployment.UserId, Name = deployment.ProjectName, Type = DockerPanel.Domain.Enums.ProjectType.DockerContainer, ImageOrPath = imageOrPath ?? deployment.ProjectName, HostPort = hostPort ?? 0, ContainerPort = hostPort, DockerContainerId = containerId, Status = DockerPanel.Domain.Enums.ProjectStatus.Running };
            db.Projects.Add(project);
        }
        else
        {
            project.ImageOrPath = imageOrPath ?? project.ImageOrPath;
            project.HostPort = hostPort ?? project.HostPort;
            project.DockerContainerId = containerId ?? project.DockerContainerId;
            project.Status = DockerPanel.Domain.Enums.ProjectStatus.Running;
        }
        await db.SaveChangesAsync(ct);
        await deployments.SetProjectIdAsync(deployment.Id, project.Id, ct);
    }

    private static async Task WaitForComposeAsync(string directory, string projectName, Guid id, CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            var output = await ComposeDeploymentService.RunAndCaptureAsync(directory, "docker", new[] { "compose", "-p", $"{projectName}-deploy-{id:N}", "ps", "--format", "json" }, ct);
            if (output.Contains("unhealthy", StringComparison.OrdinalIgnoreCase) || output.Contains("exited", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Compose servisi sağlıklı duruma geçemedi.");
            if (!string.IsNullOrWhiteSpace(output) && !output.Contains("starting", StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException("Compose health check zaman aşımına uğradı.");
    }

    private sealed record ComposeJobRequest(string ProjectName, string ComposeContent, Dictionary<string, string>? Environment, string? ProxyService, string? DomainName, string? SubdomainName, int? ProxyPort, bool SslEnabled);
    private sealed record GitHubJobRequest(string ProjectName, string Repository, string? Reference, Dictionary<string, string>? Environment, string? DomainName, string? SubdomainName, string? ProxyService, int? ProxyPort, bool SslEnabled);
    private sealed record ContainerJobRequest(string ProjectName, string ImageName, long MemoryLimitBytes, double CpuCount, int HostPort, int? ContainerPort, string? DomainName, string? SubdomainName, bool SslEnabled);
}
