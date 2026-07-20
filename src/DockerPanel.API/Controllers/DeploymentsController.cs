using System.Security.Claims;
using System.Text.Json;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/deployments")]
public sealed class DeploymentsController : ControllerBase
{
    private readonly IDeploymentService _deployments;
    private readonly IComposeAnalyzerService _analyzer;
    private readonly IComposeDeploymentService _compose;
    private readonly IGitHubDeploymentService _github;
    private readonly IDeploymentJobQueue _jobs;
    private readonly IComposeSecurityValidator _security;

    public DeploymentsController(IDeploymentService deployments, IComposeAnalyzerService analyzer, IComposeDeploymentService compose, IGitHubDeploymentService github, IDeploymentJobQueue jobs, IComposeSecurityValidator security)
    { _deployments = deployments; _analyzer = analyzer; _compose = compose; _github = github; _jobs = jobs; _security = security; }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await _deployments.GetAsync(id, cancellationToken);
        if (deployment == null || (!IsAdmin() && deployment.UserId != UserId())) return NotFound();
        return Ok(deployment);
    }

    [HttpGet("{id:guid}/steps")]
    public async Task<IActionResult> Steps(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await _deployments.GetAsync(id, cancellationToken);
        if (deployment == null || (!IsAdmin() && deployment.UserId != UserId())) return NotFound();
        return Ok(deployment.Steps.OrderBy(s => s.Order));
    }

    [HttpGet("{id:guid}/logs")]
    public async Task<IActionResult> Logs(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await _deployments.GetAsync(id, cancellationToken);
        if (deployment == null || (!IsAdmin() && deployment.UserId != UserId())) return NotFound();
        return Ok(new { deployment.Id, deployment.Status, deployment.ErrorMessage, Steps = deployment.Steps.OrderBy(s => s.Order) });
    }

    [HttpPost("{id:guid}/rollback")]
    public async Task<IActionResult> Rollback(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await _deployments.GetAsync(id, cancellationToken);
        if (deployment == null || (!IsAdmin() && deployment.UserId != UserId())) return NotFound();
        if (deployment.Status is not (DeploymentStatus.RollbackPending or DeploymentStatus.RollbackFailed or DeploymentStatus.RollingBack)) return BadRequest(new { Message = "Bu deployment rollback durumunda değil." });
        await _deployments.RollbackAsync(id, cancellationToken);
        var result = await _deployments.GetAsync(id, cancellationToken);
        return Ok(new { result!.Status, result.ErrorMessage });
    }

    [HttpPost("{id:guid}/retry-rollback")]
    public async Task<IActionResult> RetryRollback(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await _deployments.GetAsync(id, cancellationToken);
        if (deployment == null || (!IsAdmin() && deployment.UserId != UserId())) return NotFound();
        if (deployment.Status != DeploymentStatus.RollbackFailed) return BadRequest(new { Message = "Yalnızca başarısız rollback tekrar denenebilir." });
        await _deployments.RollbackAsync(id, cancellationToken);
        var result = await _deployments.GetAsync(id, cancellationToken);
        return Ok(new { result!.Status, result.ErrorMessage });
    }

    [HttpPost("analyze-compose")]
    public IActionResult AnalyzeCompose([FromBody] ComposeAnalysisRequest request)
    {
        try { _security.Validate(request.Content); return Ok(_analyzer.Analyze(request.Content)); }
        catch (Exception ex) { return BadRequest(new { Message = ex.Message }); }
    }

    [HttpPost("compose")]
    public async Task<IActionResult> Compose([FromBody] ComposeDeployRequest request, CancellationToken cancellationToken)
    {
        if (!Security.IsValidAppName(request.ProjectName)) return BadRequest(new { Message = "Geçersiz proje adı." });
        try { _security.Validate(request.ComposeContent); }
        catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
        var deployment = await _deployments.CreateAsync(UserId(), request.ProjectName, DeploymentSourceType.DockerCompose, sourceReference: "inline-compose", cancellationToken: cancellationToken);
        await _deployments.SetRequestAsync(deployment.Id, JsonSerializer.Serialize(request), cancellationToken);
        await _jobs.EnqueueAsync(deployment.Id, cancellationToken);
        return Accepted(new { deployment.Id, deployment.Status });
    }

    [HttpPost("github")]
    public async Task<IActionResult> GitHub([FromBody] GitHubDeployRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Repository, UriKind.Absolute, out var repository)) return BadRequest(new { Message = "Geçersiz repository adresi." });
        if (!Security.IsValidAppName(request.ProjectName)) return BadRequest(new { Message = "Geçersiz proje adı." });
        var deployment = await _deployments.CreateAsync(UserId(), request.ProjectName, DeploymentSourceType.GitHubRepository, sourceReference: repository.ToString(), cancellationToken: cancellationToken);
        var githubEnvironment = request.Environment is null ? new Dictionary<string, string>() : new Dictionary<string, string>(request.Environment);
        if (request.ProxyPort.HasValue) githubEnvironment["APIHUB_PROXY_PORT"] = request.ProxyPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _deployments.SetRequestAsync(deployment.Id, JsonSerializer.Serialize(request with { Environment = githubEnvironment }), cancellationToken);
        await _jobs.EnqueueAsync(deployment.Id, cancellationToken);
        return Accepted(new { deployment.Id, deployment.Status });
    }

    [HttpPost("container")]
    public async Task<IActionResult> Container([FromBody] ContainerDeployRequest request, CancellationToken cancellationToken)
    {
        if (!Security.IsValidAppName(request.ProjectName)) return BadRequest(new { Message = "Geçersiz proje adı." });
        var deployment = await _deployments.CreateAsync(UserId(), request.ProjectName, DeploymentSourceType.DockerImage, sourceReference: request.ImageName, cancellationToken: cancellationToken);
        await _deployments.SetRequestAsync(deployment.Id, JsonSerializer.Serialize(request), cancellationToken);
        await _jobs.EnqueueAsync(deployment.Id, cancellationToken);
        return Accepted(new { deployment.Id, deployment.Status });
    }

    private Guid UserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    private bool IsAdmin() => User.IsInRole(UserRole.Administrator.ToString());
}

public sealed record ComposeAnalysisRequest(string Content);
public sealed record ComposeDeployRequest(string ProjectName, string ComposeContent, Dictionary<string, string>? Environment, string? ProxyService, string? DomainName = null, string? SubdomainName = null, int? ProxyPort = null, bool SslEnabled = false);
public sealed record GitHubDeployRequest(string ProjectName, string Repository, string? Reference, Dictionary<string, string>? Environment, string? DomainName = null, string? SubdomainName = null, string? ProxyService = null, int? ProxyPort = null, bool SslEnabled = false);
public sealed record ContainerDeployRequest(string ProjectName, string ImageName, long MemoryLimitBytes, double CpuCount, int HostPort, int? ContainerPort, string? DomainName = null, string? SubdomainName = null, bool SslEnabled = false);

internal static class Security
{
    public static bool IsValidAppName(string name) => !string.IsNullOrWhiteSpace(name) && System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9_-]{0,62}$");
}
