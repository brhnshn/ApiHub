using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerPanel.Domain.Interfaces;

namespace DockerPanel.API.Controllers;

[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> GetSystemStatus([FromServices] IPushNotificationService pushService)
    {
        // 1. Docker Engine Durumu ve Versiyon Bilgisi
        bool dockerActive = false;
        string dockerVersion = "Bilinmiyor";
        string dockerApiVersion = "Bilinmiyor";

        try
        {
            Uri dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new Uri("npipe://./pipe/docker_engine")
                : new Uri("unix:///var/run/docker.sock");

            using var client = new DockerClientConfiguration(dockerUri).CreateClient();
            var versionInfo = await client.System.GetVersionAsync();
            dockerActive = true;
            dockerVersion = versionInfo.Version;
            dockerApiVersion = versionInfo.APIVersion;
        }
        catch
        {
            dockerActive = false;
        }

        // 2. Nginx Gateway Durumu
        bool nginxActive = false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            nginxActive = true; // Windows test ortamında Nginx simüle
        }
        else
        {
            // Linux ortamında gerçek active check
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

        // 3. docker-mailserver Durumu
        bool mailServerActive = false;
        try
        {
            Uri dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new Uri("npipe://./pipe/docker_engine")
                : new Uri("unix:///var/run/docker.sock");

            using var client = new DockerClientConfiguration(dockerUri).CreateClient();
            var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true });

            var mailContainer = containers.FirstOrDefault(c =>
                c.Names.Any(name => name.Contains("mailserver") || name.Contains("dockerpanel-mailserver")));

            if (mailContainer != null)
            {
                mailServerActive = mailContainer.State == "running";
            }
        }
        catch
        {
            mailServerActive = false;
        }

        return Ok(new
        {
            DockerActive = dockerActive,
            DockerVersion = dockerVersion,
            DockerApiVersion = dockerApiVersion,
            NginxActive = nginxActive,
            MailServerActive = mailServerActive,
            CpuCount = Environment.ProcessorCount,
            CpuModel = GetCpuModel(),
            IsFcmConfigured = pushService.IsFcmConfigured()
        });
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetSystemLogs([FromQuery] string source = "syslog", [FromQuery] int tail = 100)
    {
        tail = Math.Clamp(tail, 1, 500);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var simulatedLogs = source.ToLower() switch
            {
                "syslog" => new[]
                {
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [system] Sunucu ayağa kalktı.",
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [system] Docker Engine daemon başarıyla bağlandı.",
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [system] UFW güvenlik duvarı aktif edildi.",
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [system] Nginx Gateway yönlendirmesi başarıyla reload edildi."
                },
                "nginx-access" => new[]
                {
                    $"127.0.0.1 - - [{DateTime.Now:dd/MMM/yyyy:HH:mm:ss zzz}] \"GET /api/system/status HTTP/1.1\" 200 128 \"-\" \"Mozilla/5.0\"",
                    $"127.0.0.1 - - [{DateTime.Now:dd/MMM/yyyy:HH:mm:ss zzz}] \"POST /api/firewall/toggle HTTP/1.1\" 200 48 \"-\" \"Mozilla/5.0\""
                },
                "nginx-error" => new[]
                {
                    $"[{DateTime.Now:yyyy/MM/dd HH:mm:ss}] [error] 1234#0: *5 open() \"/var/www/html/favicon.ico\" failed (2: No such file or directory)"
                },
                "project-manager" => new[]
                {
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [project-manager] dotnet process started for burhansahin at port 5000",
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [project-manager] node process started for node_app at port 3000"
                },
                _ => new[] { "Geçersiz log kaynağı." }
            };

            return Ok(simulatedLogs);
        }

        string path = source.ToLower() switch
        {
            "syslog" => System.IO.File.Exists("/var/log/syslog") ? "/var/log/syslog" : "/var/log/messages",
            "nginx-access" => "/var/log/nginx/access.log",
            "nginx-error" => "/var/log/nginx/error.log",
            "project-manager" => "/var/log/project-manager.log",
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(path))
        {
            return BadRequest(new { Message = "Geçersiz log kaynağı veya log dosyası yolu bulunamadı." });
        }

        var lines = await TailFileAsync(path, tail);
        return Ok(lines);
    }

    private async Task<string[]> TailFileAsync(string filePath, int lineCount)
    {
        if (!System.IO.File.Exists(filePath))
        {
            return new[] { $"Log dosyası bulunamadı: {filePath}" };
        }

        try
        {
            var lines = new List<string>();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lines.Add(line);
                }
            }

            return lines.Skip(Math.Max(0, lines.Count - lineCount)).ToArray();
        }
        catch (Exception ex)
        {
            return new[] { $"Log dosyası okunurken hata oluştu: {ex.Message}" };
        }
    }

    private string GetCpuModel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "AMD Ryzen / Intel Core";
        }
        
        try
        {
            if (System.IO.File.Exists("/proc/cpuinfo"))
            {
                var lines = System.IO.File.ReadAllLines("/proc/cpuinfo");
                foreach (var line in lines)
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1)
                        {
                            var modelName = parts[1].Trim();
                            modelName = modelName.Replace("(R)", "").Replace("(TM)", "").Replace("CPU", "").Trim();
                            
                            // Birden fazla boşluğu temizle
                            while (modelName.Contains("  "))
                            {
                                modelName = modelName.Replace("  ", " ");
                            }
                            return modelName;
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
        return "Generic Intel Xeon";
    }
}
