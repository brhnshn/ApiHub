using System.Diagnostics;
using System.Runtime.InteropServices;
using Docker.DotNet;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace DockerPanel.API.Helpers;

public sealed class DockerHealthCheck : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new Uri("npipe://./pipe/docker_engine")
                : new Uri("unix:///var/run/docker.sock");

            using var client = new DockerClientConfiguration(endpoint).CreateClient();
            await client.System.PingAsync(cancellationToken);

            return HealthCheckResult.Healthy("Docker Engine yanit veriyor.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Docker Engine yanit vermiyor.", ex);
        }
    }
}

public sealed class NginxHealthCheck : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return HealthCheckResult.Healthy("Nginx kontrolu Windows gelistirme ortaminda simule edildi.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = "is-active nginx",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return HealthCheckResult.Unhealthy("Nginx durum komutu baslatilamadi.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();

            return process.ExitCode == 0 && output.Equals("active", StringComparison.OrdinalIgnoreCase)
                ? HealthCheckResult.Healthy("Nginx aktif.")
                : HealthCheckResult.Unhealthy($"Nginx aktif degil. Cikti: {output} {error}".Trim());
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Nginx durumu okunamadi.", ex);
        }
    }
}

public sealed class PostgreSqlHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;

    public PostgreSqlHealthCheck(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connection string bulunamadi.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            return HealthCheckResult.Healthy("PostgreSQL baglantisi basarili.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL baglantisi basarisiz.", ex);
        }
    }
}
