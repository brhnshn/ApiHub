using System.Diagnostics;
using System.Text;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Security;
using Microsoft.Extensions.Configuration;

namespace DockerPanel.Infrastructure.Services;

public sealed class ComposeDeploymentService : IComposeDeploymentService
{
    private readonly IDeploymentService _deployments;
    private readonly IConfiguration _configuration;
    private readonly IComposeSecurityValidator _security;

    public ComposeDeploymentService(IDeploymentService deployments, IConfiguration configuration, IComposeSecurityValidator security)
    {
        _deployments = deployments; _configuration = configuration; _security = security;
    }

    public async Task DeployAsync(Guid deploymentId, string projectName, string composeContent, IReadOnlyDictionary<string, string> environment, string? proxyService, CancellationToken cancellationToken = default)
    {
        InputValidator.ThrowIfInvalidProjectName(projectName, "Geçersiz proje adı.");
        _security.Validate(composeContent);
        var root = _configuration["DeploymentRoot"] ?? (OperatingSystem.IsWindows() ? Path.Combine(Path.GetTempPath(), "apihub-apps") : "/var/apihub/apps");
        var directory = Path.Combine(root, projectName, "deployments", deploymentId.ToString("N"));
        var composeProject = $"{projectName}-deploy-{deploymentId:N}";
        await _deployments.SetWorkingDirectoryAsync(deploymentId, directory, cancellationToken);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "docker-compose.yml"), composeContent, new UTF8Encoding(false), cancellationToken);
        await _deployments.AddResourceAsync(deploymentId, "directory", projectName, directory, cancellationToken: cancellationToken);
        await _deployments.AddResourceAsync(deploymentId, "file", "compose", Path.Combine(directory, "docker-compose.yml"), cancellationToken: cancellationToken);
        var envPath = Path.Combine(directory, ".env");
        await File.WriteAllLinesAsync(envPath, environment.Select(e => $"{e.Key}={e.Value}"), new UTF8Encoding(false), cancellationToken);
        await _deployments.AddResourceAsync(deploymentId, "file", "env", envPath, cancellationToken: cancellationToken);
        // Register before the first Docker command so a partial pull/up can always be compensated.
        await _deployments.AddResourceAsync(deploymentId, "compose-project", composeProject, composeProject, cancellationToken: cancellationToken);

        await RunAsync(directory, "docker", new[] { "compose", "-p", composeProject, "config" }, cancellationToken);
        await RunAsync(directory, "docker", new[] { "compose", "-p", composeProject, "pull" }, cancellationToken);
        await RunAsync(directory, "docker", new[] { "compose", "-p", composeProject, "up", "-d" }, cancellationToken);
    }

    public static async Task RunAsync(string workingDirectory, string fileName, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo { FileName = fileName, WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{fileName} başlatılamadı.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} başarısız: {error.Trim()}\n{output.Trim()}");
    }

    public static async Task<string> RunAndCaptureAsync(string workingDirectory, string fileName, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo { FileName = fileName, WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{fileName} başlatılamadı.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{fileName} işlemi başarısız oldu: {error.Trim()}");
        return output;
    }
}
