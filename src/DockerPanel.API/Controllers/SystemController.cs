using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Controllers;

[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    [HttpGet("verify-host")]
    public async Task<IActionResult> VerifyHost([FromQuery] string host, [FromServices] DockerPanelDbContext dbContext)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Ok(new { IsPanel = false });
        }

        var hostname = host.Split(':')[0].ToLower();

        if (hostname == "localhost" || hostname == "127.0.0.1" || hostname == "::1")
        {
            return Ok(new { IsPanel = true });
        }

        bool isProjectDomain = await dbContext.Subdomains.AnyAsync(s => 
            (s.SubdomainName + "." + s.DomainName).ToLower() == hostname ||
            s.DomainName.ToLower() == hostname);

        return Ok(new { IsPanel = !isProjectDomain });
    }
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

    private static bool IsDirectoryAccessible(string path)
    {
        try
        {
            Directory.GetFileSystemEntries(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // In-memory terminal session store (per-user working directory)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _userWorkingDirs = new();

    [HttpPost("terminal/run")]
    public async Task<IActionResult> RunTerminalCommand([FromBody] RunCommandRequest request)
    {
        var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdStr))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return BadRequest(new { Message = "Komut boş olamaz." });
        }

        var command = request.Command.Trim();
        var outputLines = new List<string>();

        // Get or initialize working directory for this user
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string defaultDir = isWindows ? "C:\\" : "/";
        string workingDir = _userWorkingDirs.GetOrAdd(userIdStr, defaultDir);

        // Validate that workingDir still exists and is accessible, reset if not
        try
        {
            if (!Directory.Exists(workingDir) || !IsDirectoryAccessible(workingDir))
            {
                workingDir = defaultDir;
                _userWorkingDirs[userIdStr] = workingDir;
            }
        }
        catch
        {
            workingDir = defaultDir;
            _userWorkingDirs[userIdStr] = workingDir;
        }

        // Handle 'cd' command specially to maintain working directory state
        if (command.StartsWith("cd", StringComparison.OrdinalIgnoreCase))
        {
            var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 || parts[1] == "~")
            {
                // cd with no args or cd ~ -> go to process home or /
                string homeDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(homeDir) || !Directory.Exists(homeDir) || !IsDirectoryAccessible(homeDir))
                    homeDir = isWindows ? "C:\\" : "/";
                workingDir = homeDir;
            }
            else if (parts[1] == "-")
            {
                // cd - not supported in stateless mode
                outputLines.Add("bash: cd -: OLDPWD not set");
                return Ok(new TerminalResponse { Output = outputLines, WorkingDir = workingDir });
            }
            else
            {
                string target = parts[1].Trim().Trim('"').Trim('\'');
                string newPath;
                if (Path.IsPathRooted(target))
                {
                    newPath = target;
                }
                else
                {
                    newPath = Path.GetFullPath(Path.Combine(workingDir, target));
                }

                if (Directory.Exists(newPath) && IsDirectoryAccessible(newPath))
                {
                    workingDir = newPath;
                }
                else
                {
                    outputLines.Add($"bash: cd: {target}: No such file or directory or Permission denied");
                    return Ok(new TerminalResponse { Output = outputLines, WorkingDir = workingDir });
                }
            }
            _userWorkingDirs[userIdStr] = workingDir;
            return Ok(new TerminalResponse { Output = outputLines, WorkingDir = workingDir });
        }

        try
        {
            ProcessStartInfo psi;
            if (isWindows)
            {
                psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir
                };
            }
            else
            {
                // Find bash dynamically
                string bashPath = "/bin/bash";
                foreach (var candidate in new[] { "/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash", "/bin/sh", "/usr/bin/sh" })
                {
                    if (System.IO.File.Exists(candidate))
                    {
                        bashPath = candidate;
                        break;
                    }
                }

                // Ensure workingDir is accessible, fallback to /
                string safeWorkDir = workingDir;
                try { if (!Directory.Exists(safeWorkDir) || !IsDirectoryAccessible(safeWorkDir)) safeWorkDir = "/"; } catch { safeWorkDir = "/"; }
                if (safeWorkDir != workingDir)
                {
                    workingDir = safeWorkDir;
                    _userWorkingDirs[userIdStr] = workingDir;
                }

                psi = new ProcessStartInfo
                {
                    FileName = bashPath,
                    Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = safeWorkDir
                };
            }

            using Process? process = await Task.Run(() =>
            {
                try
                {
                    return Process.Start(psi);
                }
                catch (Exception) when (!isWindows && psi.WorkingDirectory != "/")
                {
                    psi.WorkingDirectory = "/";
                    workingDir = "/";
                    _userWorkingDirs[userIdStr] = workingDir;
                    return Process.Start(psi);
                }
            });

            if (process != null)
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (!string.IsNullOrEmpty(stdout))
                {
                    outputLines.AddRange(stdout.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None));
                }
                if (!string.IsNullOrEmpty(stderr))
                {
                    outputLines.AddRange(stderr.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None));
                }
            }
        }
        catch (Exception ex)
        {
            outputLines.Add($"Komut yürütülürken hata oluştu: {ex.Message}");
        }

        return Ok(new TerminalResponse { Output = outputLines, WorkingDir = workingDir });
    }

    [HttpPost("terminal/reset")]
    public IActionResult ResetTerminalSession()
    {
        var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userIdStr))
        {
            _userWorkingDirs.TryRemove(userIdStr, out _);
        }
        return Ok();
    }

    [HttpPost("terminal/unlock")]
    public async Task<IActionResult> UnlockTerminal([FromBody] UnlockRequest request, [FromServices] DockerPanelDbContext dbContext)
    {
        var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId))
        {
            return Forbid();
        }

        var user = await dbContext.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound(new { Message = "Kullanıcı bulunamadı." });
        }

        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<DockerPanel.Domain.Entities.User>();
        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
        {
            return BadRequest(new { Message = "Geçersiz şifre!" });
        }

        return Ok(new { Success = true });
    }

    public class UnlockRequest
    {
        public string Password { get; set; } = string.Empty;
    }

    public class RunCommandRequest
    {
        public string Command { get; set; } = string.Empty;
    }

    public class TerminalResponse
    {
        public List<string> Output { get; set; } = new();
        public string WorkingDir { get; set; } = string.Empty;
    }
}
