using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Helpers;

public static class DatabaseSyncHelper
{
    public static async Task SyncExistingSystemDataAsync(IServiceProvider services)
    {
        // 0. Log temizliği tetikleme (Tek seferlik disk doluluk riski azaltma)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                Console.WriteLine("[Sync] Log temizliği tetikleniyor...");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sudo",
                    Arguments = "-n /usr/local/bin/project-manager.sh clean-logs",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine("[Sync] Log temizliği başarıyla tamamlandı.");
                    }
                    else
                    {
                        string err = await process.StandardError.ReadToEndAsync();
                        Console.WriteLine($"[Sync] Log temizliği başarısız oldu (ExitCode: {process.ExitCode}): {err}");
                    }
                }
            }
            catch (Exception logEx)
            {
                Console.WriteLine($"[Sync] Log temizleme tetiklenirken hata oluştu: {logEx.Message}");
            }
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DockerPanelDbContext>();
        var processManagerService = scope.ServiceProvider.GetRequiredService<IProcessManagerService>();

        // 1. En az bir kullanıcı var mı kontrol et, yoksa eşitleme yapamayız (UserId lazım)
        var defaultUser = await db.Users.FirstOrDefaultAsync();
        if (defaultUser == null)
        {
            Console.WriteLine("[Sync] Veritabanında henüz kullanıcı bulunmadığı için eşitleme adımı atlandı.");
            return;
        }

        Console.WriteLine("[Sync] Mevcut sistem projeleri ve subdomain'leri veritabanı ile eşitleniyor...");

        // 2. /etc/project-manager/projects.conf oku (Native Projeler)
        string confPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "project-manager", "projects.conf")
            : "/etc/project-manager/projects.conf";

        var config = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(confPath))
        {
            try
            {
                config = await ParseConfigAsync(confPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sync Hatası] Config dosyası ayrıştırılırken hata: {ex.Message}");
            }
        }

        try
        {
            // Veritabanındaki tüm projeleri çekelim
            var allProjects = await db.Projects.ToListAsync();
            var processedProjectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in allProjects)
            {
                processedProjectNames.Add(project.Name);

                // Eşitleme başlangıcında, stuck kalan her türlü Provisioning durumunu Stopped yapalım.
                // Eğer Native ise ve config'de varsa zaten aşağıda Running yapılacaktır.
                if (project.Status == ProjectStatus.Provisioning)
                {
                    project.Status = ProjectStatus.Stopped;
                    project.StartedAt = null;
                    Console.WriteLine($"[Sync] Stuck kalan Provisioning durumundaki proje durdurulduya çekildi: {project.Name}");
                }

                if (project.Type == ProjectType.NativeProject)
                {
                    // project.conf dosyasında bu proje var mı?
                    if (config.TryGetValue(project.Name, out var details) &&
                        details.TryGetValue("port", out var portStr) &&
                        int.TryParse(portStr, out int port))
                    {
                        details.TryGetValue("path", out var pathStr);
                        string targetPath = pathStr ?? "";

                        bool isRunning = await processManagerService.IsProcessRunningAsync(project.Name);
                        if (isRunning)
                        {
                            if (project.Status != ProjectStatus.Running)
                            {
                                project.Status = ProjectStatus.Running;
                                Console.WriteLine($"[Sync] OS üzerinde çalışan Native proje durumu Running olarak güncellendi: {project.Name}");
                            }
                            if (!project.StartedAt.HasValue)
                            {
                                project.StartedAt = DateTimeOffset.UtcNow;
                            }
                        }
                        else
                        {
                            if (project.Status == ProjectStatus.Running || project.Status == ProjectStatus.Provisioning)
                            {
                                project.Status = ProjectStatus.Stopped;
                                project.StartedAt = null;
                                Console.WriteLine($"[Sync] OS üzerinde çalışmayan Native proje durumu Stopped olarak güncellendi: {project.Name}");
                            }
                        }

                        if (project.InternalPort != port || project.ImageOrPath != targetPath)
                        {
                            project.InternalPort = port;
                            project.ImageOrPath = targetPath;
                            Console.WriteLine($"[Sync] Mevcut Native proje metadata güncellendi: {project.Name} (Port: {port})");
                        }
                    }
                    else
                    {
                        // project.conf dosyasında yok!
                        if (project.Status == ProjectStatus.Provisioning || project.Status == ProjectStatus.Running)
                        {
                            project.Status = ProjectStatus.Stopped;
                            project.StartedAt = null;
                            Console.WriteLine($"[Sync] Config dosyasında bulunmayan native proje durduruldu olarak güncellendi: {project.Name}");
                        }
                    }
                }
            }

            // config dosyasında olup veritabanında henüz olmayan eksik projeleri ekleyelim
            foreach (var section in config)
            {
                string projectName = section.Key;
                if (!processedProjectNames.Contains(projectName))
                {
                    var details = section.Value;
                    if (details.TryGetValue("port", out var portStr) && int.TryParse(portStr, out int port))
                    {
                        details.TryGetValue("path", out var pathStr);
                        string targetPath = pathStr ?? "";

                        bool isRunning = await processManagerService.IsProcessRunningAsync(projectName);

                        var newProject = new Project
                        {
                            UserId = defaultUser.Id,
                            Name = projectName,
                            Type = ProjectType.NativeProject,
                            ImageOrPath = targetPath,
                            InternalPort = port,
                            MemoryLimitBytes = 536870912, // 512 MB varsayılan
                            CpuCount = 0.5,
                            Status = isRunning ? ProjectStatus.Running : ProjectStatus.Stopped,
                            StartedAt = isRunning ? DateTimeOffset.UtcNow : null,
                            CreatedAt = DateTimeOffset.UtcNow
                        };
                        db.Projects.Add(newProject);
                        Console.WriteLine($"[Sync] Eksik Native Proje Veritabanına Eklendi: {projectName} (Port: {port}, Çalışıyor: {isRunning})");
                    }
                }
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sync Hatası] Proje veritabanı eşitleme sırasında genel hata: {ex.Message}");
        }

        // 3. /etc/nginx/sites-available altındaki tüm vhost dosyalarını oku (Subdomain Yönlendirmeleri)
        string nginxDir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "opt_dockerpanel", "etc", "nginx", "sites-available")
            : "/etc/nginx/sites-available";

        if (Directory.Exists(nginxDir))
        {
            try
            {
                var files = Directory.GetFiles(nginxDir);
                foreach (var file in files)
                {
                    string filename = Path.GetFileName(file);
                    // "nginx-template.conf" veya API'nin kendi konfigürasyonunu es geç
                    if (filename.Equals("nginx-template.conf", StringComparison.OrdinalIgnoreCase) || 
                        filename.Contains("api") || 
                        filename.Contains("panel"))
                    {
                        continue;
                    }

                    var lines = await File.ReadAllLinesAsync(file, Encoding.UTF8);
                    string? currentServerName = null;
                    bool sslEnabled = false;

                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        
                        // SSL durum tespiti (bu server bloğu içinde listen 443 veya ssl görürsek)
                        if (trimmed.Contains("listen 443") || trimmed.Contains("ssl"))
                        {
                            sslEnabled = true;
                        }

                        if (trimmed.StartsWith("server_name"))
                        {
                            // "server_name sub.domain.com;" veya "server_name sub.domain.com sub2.domain.com;"
                            var cleanLine = trimmed.Replace("server_name", "").Replace(";", "").Trim();
                            var parts = cleanLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                currentServerName = parts[0];
                            }
                        }
                        else if (trimmed.StartsWith("proxy_pass") && !string.IsNullOrEmpty(currentServerName))
                        {
                            var match = Regex.Match(trimmed, @"proxy_pass\s+http://(?:127\.0\.0\.1|localhost):(\d+);");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int port))
                            {
                                // Subdomain ve Domain'e ayrıştır (örneğin sub.domain.com veya domain.com)
                                var domainParts = currentServerName.Split('.');
                                if (domainParts.Length >= 2)
                                {
                                    string subdomainName = domainParts[0];
                                    string domainName = string.Join(".", domainParts.Skip(1));

                                    // Veritabanında bu subdomain zaten var mı kontrol et
                                    var subExists = await db.Subdomains.AnyAsync(s => 
                                        s.SubdomainName.ToLower() == subdomainName.ToLower() && 
                                        s.DomainName.ToLower() == domainName.ToLower());

                                    if (!subExists)
                                    {
                                        // Bu porta sahip projeyi bul
                                        var matchingProject = await db.Projects.FirstOrDefaultAsync(p => p.InternalPort == port);
                                        if (matchingProject != null)
                                        {
                                            var newSub = new Subdomain
                                            {
                                                UserId = defaultUser.Id,
                                                ProjectId = matchingProject.Id,
                                                SubdomainName = subdomainName.ToLower(),
                                                DomainName = domainName.ToLower(),
                                                SslEnabled = sslEnabled,
                                                CreatedAt = DateTimeOffset.UtcNow
                                            };
                                            db.Subdomains.Add(newSub);
                                            Console.WriteLine($"[Sync] Eksik Subdomain Eklendi: {subdomainName}.{domainName} -> {matchingProject.Name} (Port: {port})");
                                        }
                                    }
                                }
                                
                                // Temizle
                                currentServerName = null;
                                sslEnabled = false;
                            }
                        }
                    }
                }
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sync Hatası] Nginx subdomain eşitleme sırasında hata: {ex.Message}");
            }
        }

        Console.WriteLine("[Sync] Sistem eşitleme işlemi başarıyla tamamlandı!");
    }

    private static async Task<Dictionary<string, Dictionary<string, string>>> ParseConfigAsync(string path)
    {
        var config = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;

            var parts = trimmed.Split('|');
            if (parts.Length >= 2)
            {
                string projectName = parts[0].Trim();
                string pathStr = parts[1].Trim();
                string startCommand = parts.Length >= 3 ? parts[2].Trim() : string.Empty;

                var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["path"] = pathStr,
                    ["start_command"] = startCommand
                };

                // Extract port using regex (e.g. :5030, localhost:5030, or 127.0.0.1:5030)
                var portMatch = Regex.Match(startCommand, @"(?::|localhost:)(\d+)");
                if (portMatch.Success)
                {
                    details["port"] = portMatch.Groups[1].Value;
                }
                else
                {
                    // Fallback: try to find any 4-5 digit number in the start command
                    var fallbackMatch = Regex.Match(startCommand, @"\b(\d{4,5})\b");
                    if (fallbackMatch.Success)
                    {
                        details["port"] = fallbackMatch.Groups[1].Value;
                    }
                }

                config[projectName] = details;
            }
        }
        return config;
    }
}
