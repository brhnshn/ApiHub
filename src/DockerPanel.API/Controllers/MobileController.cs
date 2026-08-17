using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/mobile")]
public class MobileController : ControllerBase
{
    private readonly DockerPanelDbContext _context;
    private readonly IProjectContainerService _containerService;
    private readonly IFirewallService _firewallService;
    private readonly IProcessManagerService _processManagerService;
    private readonly ISystemMetricsService _metricsService;

    public MobileController(
        DockerPanelDbContext context,
        IProjectContainerService containerService,
        IFirewallService firewallService,
        IProcessManagerService processManagerService,
        ISystemMetricsService metricsService)
    {
        _context = context;
        _containerService = containerService;
        _firewallService = firewallService;
        _processManagerService = processManagerService;
        _metricsService = metricsService;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        // 1. Get CPU & RAM & Disk
        var metrics = await _metricsService.GetCurrentMetricsAsync();
        var cpuUsage = metrics.Cpu;
        var ramPct = metrics.RamPercentage;
        var ramUsed = metrics.RamUsedGb;
        var ramTotal = metrics.RamTotalGb;
        var diskPct = metrics.DiskUsedPercentage;
        var diskUsed = metrics.DiskUsedGb;
        var diskTotal = metrics.DiskTotalGb;

        // 2. Docker & Nginx Status
        var dockerActive = false;
        var dockerVersion = "Bilinmiyor";
        try
        {
            Uri dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new Uri("npipe://./pipe/docker_engine")
                : new Uri("unix:///var/run/docker.sock");

            using var client = new DockerClientConfiguration(dockerUri).CreateClient();
            var versionInfo = await client.System.GetVersionAsync();
            dockerActive = true;
            dockerVersion = versionInfo.Version;
        }
        catch
        {
            dockerActive = false;
        }

        var nginxActive = false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            nginxActive = true;
        }
        else
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "systemctl",
                    Arguments = "is-active nginx",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    string output = (await process.StandardOutput.ReadToEndAsync()).Trim();
                    nginxActive = output == "active";
                }
            }
            catch
            {
                nginxActive = false;
            }
        }

        // 3. Projects and Containers Statuses
        var projects = await _context.Projects.ToListAsync();
        var projectList = new List<object>();

        foreach (var proj in projects)
        {
            var isRunning = false;
            var details = "Bilinmiyor";

            if (proj.Type == ProjectType.DockerContainer && !string.IsNullOrEmpty(proj.DockerContainerId))
            {
                try
                {
                    isRunning = await _containerService.IsContainerRunningAsync(proj.DockerContainerId);
                    details = isRunning ? "Çalışıyor (Docker)" : "Durduruldu (Docker)";
                }
                catch
                {
                    details = "Docker Hatası";
                }
            }
            else if (proj.Type == ProjectType.NativeProject)
            {
                try
                {
                    isRunning = await _processManagerService.IsProcessRunningAsync(proj.Name);
                    details = isRunning ? "Çalışıyor (Native)" : "Durduruldu (Native)";
                }
                catch
                {
                    details = "Süreç Hatası";
                }
            }

            projectList.Add(new
            {
                proj.Id,
                proj.Name,
                proj.Type,
                proj.ImageOrPath,
                proj.Status,
                IsRunning = isRunning,
                Details = details,
                proj.StartedAt,
                proj.MemoryLimitBytes,
                proj.CpuCount,
                proj.HostPort
            });
        }

        // 4. Domains and Subdomains Status
        var subdomains = await _context.Subdomains.Include(s => s.Project).ToListAsync();
        var subdomainList = subdomains.Select(s => new
        {
            s.Id,
            FullUrl = $"http://{(string.IsNullOrEmpty(s.SubdomainName) || s.SubdomainName == "@" ? "" : s.SubdomainName + ".")}{s.DomainName}",
            s.SubdomainName,
            s.DomainName,
            Port = s.Project?.HostPort ?? 80,
            s.CreatedAt
        }).ToList();

        // 5. Open Ports (Firewall Rules)
        var firewallActive = false;
        var rules = new List<FirewallRuleDto>();
        try
        {
            firewallActive = await _firewallService.IsFirewallActiveAsync();
            var rawRules = await _firewallService.GetRulesAsync();
            rules = rawRules.ToList();
        }
        catch
        {
            // Fallback
        }

        return Ok(new
        {
            Server = new
            {
                CpuUsage = cpuUsage,
                RamPercentage = ramPct,
                RamUsedGb = ramUsed,
                RamTotalGb = ramTotal,
                DiskPercentage = diskPct,
                DiskUsedGb = diskUsed,
                DiskTotalGb = diskTotal,
                DockerActive = dockerActive,
                DockerVersion = dockerVersion,
                NginxActive = nginxActive,
                FirewallActive = firewallActive
            },
            Projects = projectList,
            Domains = subdomainList,
            FirewallRules = rules
        });
    }

    [HttpPost("containers/{name}/control")]
    public async Task<IActionResult> ControlContainer(string name, [FromBody] ContainerControlRequest request)
    {
        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Name == name);
        if (project == null)
        {
            return NotFound(new { Message = "Proje bulunamadı!" });
        }

        if (project.Type != ProjectType.DockerContainer || string.IsNullOrEmpty(project.DockerContainerId))
        {
            return BadRequest(new { Message = "Bu proje bir Docker konteyneri değil veya ID'si yok!" });
        }

        try
        {
            var action = request.Action?.ToLower();
            if (action == "start")
            {
                await _containerService.StartContainerAsync(project.DockerContainerId);
                project.Status = ProjectStatus.Running;
                project.StartedAt = DateTimeOffset.UtcNow;
            }
            else if (action == "stop")
            {
                await _containerService.StopContainerAsync(project.DockerContainerId);
                project.Status = ProjectStatus.Stopped;
            }
            else if (action == "restart")
            {
                await _containerService.StopContainerAsync(project.DockerContainerId);
                await _containerService.StartContainerAsync(project.DockerContainerId);
                project.Status = ProjectStatus.Running;
                project.StartedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                return BadRequest(new { Message = "Geçersiz eylem! Sadece 'start', 'stop' veya 'restart' desteklenir." });
            }

            await _context.SaveChangesAsync();
            return Ok(new { Message = $"Konteyner başarıyla '{action}' edildi.", Status = project.Status.ToString() });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = $"Konteyner yönetilirken hata oluştu: {ex.Message}" });
        }
    }
}

public class ContainerControlRequest
{
    public string Action { get; set; } = string.Empty;
}
