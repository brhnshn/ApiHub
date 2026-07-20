using System.Text.RegularExpressions;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Security;
using Microsoft.Extensions.Configuration;

namespace DockerPanel.Infrastructure.Services;

public sealed class GitHubDeploymentService : IGitHubDeploymentService
{
    private readonly IDeploymentService _deployments;
    private readonly IComposeDeploymentService _compose;
    private readonly IComposeAnalyzerService _analyzer;
    private readonly IConfiguration _configuration;
    private readonly IProjectContainerService _containers;

    public GitHubDeploymentService(IDeploymentService deployments, IComposeDeploymentService compose, IComposeAnalyzerService analyzer, IConfiguration configuration, IProjectContainerService containers)
    { _deployments = deployments; _compose = compose; _analyzer = analyzer; _configuration = configuration; _containers = containers; }

    public async Task DeployAsync(Guid deploymentId, string projectName, Uri repository, string? reference, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken = default)
    {
        if (!repository.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || repository.Segments.Length < 3) throw new ArgumentException("Yalnızca public GitHub repository desteklenir.");
        InputValidator.ThrowIfInvalidProjectName(projectName, "Geçersiz proje adı.");
        var root = _configuration["DeploymentRoot"] ?? (OperatingSystem.IsWindows() ? Path.Combine(Path.GetTempPath(), "apihub-apps") : "/var/apihub/apps");
        var directory = Path.Combine(root, projectName, "deployments", deploymentId.ToString("N"));
        Directory.CreateDirectory(directory);
        await ComposeDeploymentService.RunAsync(Path.GetDirectoryName(directory)!, "git", new[] { "clone", "--depth", "1", "--no-tags", repository.ToString(), directory }, cancellationToken);
        await _deployments.AddResourceAsync(deploymentId, "directory", projectName, directory, cancellationToken: cancellationToken);
        await _deployments.SetWorkingDirectoryAsync(deploymentId, directory, cancellationToken);
        if (!string.IsNullOrWhiteSpace(reference))
        {
            await ComposeDeploymentService.RunAsync(directory, "git", new[] { "fetch", "--depth", "1", "origin", reference }, cancellationToken);
            await ComposeDeploymentService.RunAsync(directory, "git", new[] { "checkout", "--detach", "FETCH_HEAD" }, cancellationToken);
        }
        var sha = await ReadGitValueAsync(directory, "rev-parse", "HEAD", cancellationToken);
        var deployment = await _deployments.GetAsync(deploymentId, cancellationToken) ?? throw new InvalidOperationException("Deployment bulunamadı.");
        deployment.CommitSha = sha;
        await _deployments.SetStatusAsync(deploymentId, deployment.Status, cancellationToken: cancellationToken);

        var composePath = Directory.EnumerateFiles(directory, "*compose*.y*ml", SearchOption.AllDirectories).FirstOrDefault();
        if (composePath != null)
        {
            var content = await File.ReadAllTextAsync(composePath, cancellationToken);
            _analyzer.Analyze(content);
            await _compose.DeployAsync(deploymentId, projectName, content, environment, null, cancellationToken);
            return;
        }
        var dockerfile = Directory.EnumerateFiles(directory, "Dockerfile", SearchOption.AllDirectories).FirstOrDefault();
        if (dockerfile == null) throw new FileNotFoundException("Repository içinde compose dosyası veya Dockerfile bulunamadı.");
        if (!environment.TryGetValue("APIHUB_PROXY_PORT", out var proxyPortValue) || !int.TryParse(proxyPortValue, out var proxyPort) || proxyPort is < 1 or > 65535)
            throw new InvalidOperationException("Dockerfile deployment için APIHUB_PROXY_PORT environment değeri gereklidir.");
        var image = $"apihub/{projectName}:{sha[..Math.Min(12, sha.Length)]}";
        await ComposeDeploymentService.RunAsync(Path.GetDirectoryName(dockerfile)!, "docker", new[] { "build", "-t", image, "." }, cancellationToken);
        await _deployments.AddResourceAsync(deploymentId, "image", image, image, cancellationToken: cancellationToken);
        var containerName = $"{projectName}-deploy-{deploymentId:N}";
        var containerPort = await _containers.GetImageExposedPortAsync(image) ?? proxyPort;
        var containerId = await _containers.ProvisionContainerAsync(containerName, image, 0, 1, proxyPort, containerPort);
        await _deployments.AddResourceAsync(deploymentId, "container", containerName, containerId, cancellationToken: cancellationToken);
        if (!await _containers.IsContainerRunningAsync(containerId)) throw new InvalidOperationException("GitHub Dockerfile container başlatılamadı.");
    }

    private static async Task<string> ReadGitValueAsync(string directory, params object[] values)
    {
        var args = values.Select(Convert.ToString).Where(v => v != null).Cast<string>();
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = "git", WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("git başlatılamadı.");
        var output = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Git commit bilgisi alınamadı.");
        return output.Trim();
    }
}
