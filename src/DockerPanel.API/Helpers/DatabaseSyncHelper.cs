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
                                try
                                {
                                    Console.WriteLine($"[Sync] OS üzerinde çalışmayan Native proje başlatılıyor: {project.Name}");
                                    await processManagerService.StartProcessAsync(project.Name);
                                    project.Status = ProjectStatus.Running;
                                    project.StartedAt = DateTimeOffset.UtcNow;
                                    Console.WriteLine($"[Sync] OS üzerinde çalışmayan Native proje başarıyla başlatıldı: {project.Name}");
                                }
                                catch (Exception startEx)
                                {
                                    project.Status = ProjectStatus.Error;
                                    project.StartedAt = null;
                                    Console.WriteLine($"[Sync Hatası] Proje başlatılamadı, Hata durumuna alındı: {project.Name}. Detay: {startEx.Message}");
                                }
                            }
                        }

                        if (project.HostPort != port || project.ImageOrPath != targetPath)
                        {
                            project.HostPort = port;
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
                            HostPort = port,
                            ContainerPort = null,
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
                                        var matchingProject = await db.Projects.FirstOrDefaultAsync(p => p.HostPort == port);
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

        await SeedDefaultMaintenancePagesAsync(db, defaultUser.Id);
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

    private static async Task SeedDefaultMaintenancePagesAsync(DockerPanelDbContext db, Guid userId)
    {
        if (await db.MaintenancePages.AnyAsync()) return;

        var templates = new List<MaintenancePage>
        {
            new MaintenancePage
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = "Sistem Bakımda",
                HtmlContent = @"<!DOCTYPE html>
<html lang=""tr"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Sistem Bakımda</title>
<style>
* { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: system-ui, -apple-system, sans-serif; background: #0b0f17; color: #e2e8f0; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 24px; }
.card { background: rgba(23,32,48,0.8); border: 1px solid rgba(255,255,255,0.08); border-radius: 20px; padding: 48px 36px; max-width: 480px; width: 100%; text-align: center; box-shadow: 0 20px 40px rgba(0,0,0,0.6); backdrop-filter: blur(12px); }
.badge { display: inline-flex; align-items: center; gap: 8px; background: rgba(245,158,11,0.1); color: #fbbf24; border: 1px solid rgba(245,158,11,0.25); padding: 6px 16px; border-radius: 20px; font-size: 13px; font-weight: 600; margin-bottom: 24px; }
.dot { width: 8px; height: 8px; background-color: #f59e0b; border-radius: 50%; animation: blink 1.8s infinite; }
@keyframes blink { 0%,100% { opacity: 1; transform: scale(1); } 50% { opacity: 0.3; transform: scale(0.8); } }
h1 { font-size: 26px; font-weight: 800; color: #fff; margin-bottom: 12px; letter-spacing: -0.5px; }
p { font-size: 15px; color: #94a3b8; line-height: 1.6; margin-bottom: 28px; }
.footer { font-size: 12px; color: #64748b; border-top: 1px solid rgba(255,255,255,0.06); padding-top: 20px; }
</style>
</head>
<body>
<div class=""card"">
<div class=""badge""><span class=""dot""></span> Planlı Bakım</div>
<h1>Sistem Bakımdadır</h1>
<p>Sistemlerimizi daha iyi bir deneyim sunmak için güncelliyoruz. Kısa süre içinde tekrar hizmetinizde olacağız.</p>
<div class=""footer"">DockerPanel Sunucu Yönetimi</div>
</div>
</body>
</html>",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new MaintenancePage
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = "Yakında Açılıyor",
                HtmlContent = @"<!DOCTYPE html>
<html lang=""tr"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Yakında Açılıyor</title>
<style>
* { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: system-ui, -apple-system, sans-serif; background: #090d16; color: #e2e8f0; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 24px; }
.card { background: rgba(30,41,59,0.5); border: 1px solid rgba(255,255,255,0.1); border-radius: 24px; padding: 48px 36px; max-width: 520px; width: 100%; text-align: center; box-shadow: 0 25px 50px rgba(0,0,0,0.5); }
.rocket { font-size: 48px; margin-bottom: 16px; display: inline-block; animation: float 3s ease-in-out infinite; }
@keyframes float { 0%,100% { transform: translateY(0); } 50% { transform: translateY(-10px); } }
h1 { font-size: 28px; font-weight: 800; color: #fff; margin-bottom: 12px; }
p { font-size: 15px; color: #94a3b8; line-height: 1.6; margin-bottom: 32px; }
.timer { display: flex; justify-content: center; gap: 16px; margin-bottom: 32px; }
.box { background: rgba(15,23,42,0.8); border: 1px solid rgba(255,255,255,0.06); padding: 12px 18px; border-radius: 12px; min-width: 70px; }
.num { font-size: 22px; font-weight: 800; color: #60a5fa; }
.label { font-size: 11px; color: #64748b; text-transform: uppercase; margin-top: 4px; }
.footer { font-size: 12px; color: #475569; }
</style>
</head>
<body>
<div class=""card"">
<div class=""rocket"">🚀</div>
<h1>Yakında Hizmetinizdeyiz</h1>
<p>Yeni versiyonumuz üzerinde son kontrolleri yapıyoruz. Çok yakında yayındayız!</p>
<div class=""timer"">
<div class=""box""><div class=""num"">00</div><div class=""label"">Gün</div></div>
<div class=""box""><div class=""num"">02</div><div class=""label"">Saat</div></div>
<div class=""box""><div class=""num"">45</div><div class=""label"">Dakika</div></div>
</div>
<div class=""footer"">Bizi takip etmeye devam edin.</div>
</div>
</body>
</html>",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new MaintenancePage
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = "Geçici Kapalı",
                HtmlContent = @"<!DOCTYPE html>
<html lang=""tr"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Geçici Olarak Kapalı</title>
<style>
* { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: system-ui, -apple-system, sans-serif; background: #0f172a; color: #cbd5e1; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 24px; }
.card { background: #1e293b; border: 1px solid #334155; border-radius: 16px; padding: 40px 32px; max-width: 440px; width: 100%; text-align: center; }
.icon { font-size: 42px; color: #ef4444; margin-bottom: 16px; }
h1 { font-size: 22px; font-weight: 700; color: #f8fafc; margin-bottom: 12px; }
p { font-size: 14px; color: #94a3b8; line-height: 1.6; margin-bottom: 24px; }
.contact { font-size: 13px; color: #38bdf8; text-decoration: none; font-weight: 600; }
</style>
</head>
<body>
<div class=""card"">
<div class=""icon"">⏸</div>
<h1>Servis Geçici Olarak Kapalıdır</h1>
<p>Bu servis şu anda aktif değildir. Acil durumlar için destek ekibimizle iletişime geçebilirsiniz.</p>
<a href=""mailto:support@example.com"" class=""contact"">Destek Ekibine Ulaşın &rarr;</a>
</div>
</body>
</html>",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new MaintenancePage
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = "Güncelleme Yapılıyor",
                HtmlContent = @"<!DOCTYPE html>
<html lang=""tr"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Güncelleme Yapılıyor</title>
<style>
* { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: system-ui, -apple-system, sans-serif; background: #0a0a0c; color: #e4e4e7; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 24px; }
.card { background: #18181b; border: 1px solid #27272a; border-radius: 20px; padding: 44px 36px; max-width: 480px; width: 100%; text-align: center; }
.gear { font-size: 40px; margin-bottom: 16px; display: inline-block; animation: spin 8s linear infinite; }
@keyframes spin { 100% { transform: rotate(360deg); } }
h1 { font-size: 24px; font-weight: 700; color: #fafafa; margin-bottom: 12px; }
p { font-size: 14px; color: #a1a1aa; line-height: 1.6; margin-bottom: 28px; }
.progress-bar { background: #27272a; border-radius: 10px; height: 8px; width: 100%; overflow: hidden; margin-bottom: 16px; }
.progress-inner { background: linear-gradient(90deg, #a855f7, #ec4899); height: 100%; width: 65%; border-radius: 10px; animation: pulse-width 2s ease-in-out infinite alternate; }
@keyframes pulse-width { 0% { width: 50%; } 100% { width: 85%; } }
.status-text { font-size: 12px; color: #71717a; }
</style>
</head>
<body>
<div class=""card"">
<div class=""gear"">⚙️</div>
<h1>Sistem Güncelleniyor</h1>
<p>Güvenlik ve performans güncellemeleri uygulanıyor. Verileriniz güvendedir.</p>
<div class=""progress-bar""><div class=""progress-inner""></div></div>
<div class=""status-text"">Güncelleme paketi yükleniyor (%75)...</div>
</div>
</body>
</html>",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new MaintenancePage
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = "Servis Devre Dışı",
                HtmlContent = @"<!DOCTYPE html>
<html lang=""tr"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Servis Devre Dışı</title>
<style>
* { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: system-ui, -apple-system, sans-serif; background: #022c22; color: #ecfdf5; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 24px; }
.card { background: rgba(6,78,59,0.8); border: 1px solid rgba(52,211,153,0.2); border-radius: 20px; padding: 44px 36px; max-width: 480px; width: 100%; text-align: center; box-shadow: 0 20px 40px rgba(0,0,0,0.4); }
.shield { font-size: 42px; margin-bottom: 16px; display: inline-block; }
h1 { font-size: 24px; font-weight: 700; color: #ffffff; margin-bottom: 12px; }
p { font-size: 14px; color: #a7f3d0; line-height: 1.6; margin-bottom: 24px; }
.badge { display: inline-flex; align-items: center; gap: 8px; background: rgba(16,185,129,0.2); color: #34d399; padding: 6px 16px; border-radius: 20px; font-size: 12px; font-weight: 600; }
</style>
</head>
<body>
<div class=""card"">
<div class=""shield"">🛡️</div>
<h1>Servis Yapılandırmada</h1>
<p>Bu servis şu anda güvenli bir şekilde yapılandırılmaktadır. Kısa süre içinde hizmete açılacaktır.</p>
<div class=""badge"">Güvenli Sunucu Yönetimi</div>
</div>
</body>
</html>",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        db.MaintenancePages.AddRange(templates);
        await db.SaveChangesAsync();
        Console.WriteLine("[Sync] 5 adet varsayılan bakım sayfası şablonu veritabanına eklendi.");
    }
}
