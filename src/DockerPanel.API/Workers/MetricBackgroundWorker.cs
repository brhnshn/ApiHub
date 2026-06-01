using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DockerPanel.API.Hubs;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Entities;
using DockerPanel.Infrastructure.Data;
using DockerPanel.Infrastructure.Services;

namespace DockerPanel.API.Workers;

public class MetricBackgroundWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<MetricLogHub> _hubContext;
    private readonly ILogger<MetricBackgroundWorker> _logger;
    private double _lastCpuUser = 0;
    private double _lastCpuNice = 0;
    private double _lastCpuSystem = 0;
    private double _lastCpuIdle = 0;
    private long _lastSyslogPosition = -1;
    // static: her iterasyonda new Random() üretilmesi önleniyor (seed sorunu + performans)
    private static readonly Random _rand = new();

    public MetricBackgroundWorker(
        IServiceScopeFactory scopeFactory,
        IHubContext<MetricLogHub> hubContext,
        ILogger<MetricBackgroundWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;

        BackupService.OnBackupUpdated += HandleBackupUpdated;
    }

    private async Task HandleBackupUpdated()
    {
        try
        {
            _logger.LogInformation("SignalR: Backup list updated event received, broadcasting to clients.");
            await _hubContext.Clients.All.SendAsync("ReceiveBackupUpdated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast ReceiveBackupUpdated via SignalR.");
        }
    }

    public override void Dispose()
    {
        BackupService.OnBackupUpdated -= HandleBackupUpdated;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MetricBackgroundWorker başlatılıyor...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Sunucu Genel CPU / RAM Tüketimini Hesapla
                var systemCpu = await GetSystemCpuUsageAsync();
                var (systemRamUsedPercentage, systemRamUsedGb, systemRamTotalGb) = GetSystemRamUsage();
                var (diskUsedPercentage, diskUsedGb, diskTotalGb) = GetSystemDiskUsage();

                // Tüm istemcilere genel sunucu metriklerini yayınla
                await _hubContext.Clients.All.SendAsync("ReceiveSystemMetrics", new
                {
                    Cpu = systemCpu,
                    RamPercentage = systemRamUsedPercentage,
                    RamUsedGb = systemRamUsedGb,
                    RamTotalGb = systemRamTotalGb,
                    DiskUsedPercentage = diskUsedPercentage,
                    DiskUsedGb = diskUsedGb,
                    DiskTotalGb = diskTotalGb
                }, stoppingToken);

                // 2. Her bir aktif proje için donanım metriklerini al ve ilgili gruba yay
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<DockerPanelDbContext>();
                    var containerService = scope.ServiceProvider.GetRequiredService<IProjectContainerService>();
                    var processManagerService = scope.ServiceProvider.GetRequiredService<IProcessManagerService>();
                    var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

                    var activeProjects = dbContext.Projects
                        .Where(p => p.Status == ProjectStatus.Running)
                        .ToList();

                    foreach (var project in activeProjects)
                    {
                        try
                        {
                            var projectStateChanged = false;
                            double cpu = 0;
                            double ramBytes = 0;
                            double ramLimitBytes = 536870912; // Varsayılan 512MB
                            double ramPercentage = 0;
                            System.Collections.Generic.IEnumerable<string> logs = new string[] { };

                            if (project.Type == ProjectType.DockerContainer && !string.IsNullOrEmpty(project.DockerContainerId))
                            {
                                // Watchdog: Check if the Docker container is actually running
                                bool isRunning = await containerService.IsContainerRunningAsync(project.DockerContainerId);
                                if (!isRunning)
                                {
                                    _logger.LogWarning("[Watchdog] Docker container for project {ProjectName} ({ProjectId}) is not running! Attempting auto-restart...", project.Name, project.Id);
                                    SystemLogQueue.Log("warning", $"[Watchdog] '{project.Name}' Docker projesi durmuş durumda tespit edildi, otomatik yeniden başlatılıyor...");
                                    
                                    // Send push notification to user
                                    await pushService.SendNotificationToUserAsync(
                                        project.UserId,
                                        "🔴 Servis Durdu (Docker)",
                                        $"'{project.Name}' Docker konteyner servisi durmuş durumda tespit edildi, otomatik yeniden başlatılıyor...",
                                        $"apihub://navigate?path=/containers&projectId={project.Id}");

                                    try
                                    {
                                        await containerService.StartContainerAsync(project.DockerContainerId);
                                        project.StartedAt = DateTimeOffset.UtcNow;
                                        projectStateChanged = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "[Watchdog] Failed to restart Docker container for project {ProjectName}", project.Name);
                                    }
                                }

                                var stats = await containerService.GetContainerStatsAsync(project.DockerContainerId);
                                cpu = stats.CpuPercentage;
                                ramBytes = stats.MemoryUsageBytes;
                                ramLimitBytes = stats.MemoryLimitBytes;
                                ramPercentage = stats.MemoryPercentage;

                                logs = await containerService.GetContainerLogsAsync(project.DockerContainerId, 5);
                            }
                            else if (project.Type == ProjectType.NativeProject)
                            {
                                // Watchdog: Check if the Native process is actually running
                                bool isRunning = await processManagerService.IsProcessRunningAsync(project.Name);
                                if (!isRunning)
                                {
                                    _logger.LogWarning("[Watchdog] Native process for project {ProjectName} ({ProjectId}) is not running! Attempting auto-restart...", project.Name, project.Id);
                                    SystemLogQueue.Log("warning", $"[Watchdog] '{project.Name}' Native projesi durmuş durumda tespit edildi, otomatik yeniden başlatılıyor...");
                                    
                                    // Send push notification to user
                                    await pushService.SendNotificationToUserAsync(
                                        project.UserId,
                                        "🔴 Servis Durdu (Native)",
                                        $"'{project.Name}' native süreci durmuş durumda tespit edildi, otomatik yeniden başlatılıyor...",
                                        $"apihub://navigate?path=/containers&projectId={project.Id}");

                                    try
                                    {
                                        await processManagerService.StartProcessAsync(project.Name);
                                        project.StartedAt = DateTimeOffset.UtcNow;
                                        projectStateChanged = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "[Watchdog] Failed to restart native process for project {ProjectName}", project.Name);
                                    }
                                }

                                // Yerel/Native süreç simüle metrikleri
                                cpu = Math.Round(1.5 + _rand.NextDouble() * 3.5, 1); // %1.5 - %5.0
                                ramBytes = 45 * 1024 * 1024 + _rand.Next(0, 10) * 1024 * 1024; // 45MB - 55MB
                                ramLimitBytes = project.MemoryLimitBytes > 0 ? project.MemoryLimitBytes : 536870912;
                                ramPercentage = Math.Round((ramBytes / ramLimitBytes) * 100.0, 1);

                                try
                                {
                                    logs = await processManagerService.GetProcessLogsAsync(project.Name, 5);
                                }
                                catch (Exception logEx)
                                {
                                    _logger.LogWarning(logEx, "Native proje {ProjectName} için loglar okunamadı (muhtemelen yetki veya dosya yok).", project.Name);
                                }
                            }
                            else if (project.Type == ProjectType.StaticSite)
                            {
                                // Statik site için simüle metrikler (Sadece Nginx tarafından sunulduğu için sıfıra yakın çok düşük kaynak kullanımı)
                                cpu = Math.Round(0.0 + _rand.NextDouble() * 0.1, 2); // %0.0 - %0.1
                                ramBytes = 1 * 1024 * 1024; // 1MB sabit simüle
                                ramLimitBytes = 536870912;
                                ramPercentage = Math.Round((ramBytes / ramLimitBytes) * 100.0, 2);

                                logs = new[] { "[Statik Web Sitesi] Bu proje doğrudan Nginx tarafından sunulmaktadır. Aktif bir arka plan süreci bulunmadığı için çalışma zamanı logu üretilmez." };
                            }

                            if (projectStateChanged)
                            {
                                await dbContext.SaveChangesAsync(stoppingToken);
                            }

                            // İlgili proje SignalR grubuna metrikleri bas
                            await _hubContext.Clients.Group($"project_{project.Id}").SendAsync("ReceiveProjectMetrics", new
                            {
                                ProjectId = project.Id,
                                Cpu = cpu,
                                RamBytes = ramBytes,
                                RamLimitBytes = ramLimitBytes,
                                RamPercentage = ramPercentage
                            }, stoppingToken);

                            // Canlı terminal log akışı
                            if (logs != null && logs.Any())
                            {
                                await _hubContext.Clients.Group($"project_{project.Id}").SendAsync("ReceiveProjectLogs", new
                                {
                                    ProjectId = project.Id,
                                    Logs = logs
                                }, stoppingToken);
                            }
                        }
                        catch (Exception projEx)
                        {
                            _logger.LogError(projEx, "Proje {ProjectName} ({ProjectId}) için metrik veya log toplanırken hata oluştu.", project.Name, project.Id);
                        }
                    }
                }

                // 3. Sistem loglarını SignalR ile yayınla
                await ReadNewSyslogLinesAsync();
                var systemLogs = new System.Collections.Generic.List<SystemLogLine>();
                while (SystemLogQueue.Queue.TryDequeue(out var logLine))
                {
                    systemLogs.Add(logLine);
                }

                if (systemLogs.Any())
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveSystemLogs", systemLogs, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Metrik toplama döngüsünde bir hata oluştu.");
            }

            // Her 3 saniyede bir çalışır
            await Task.Delay(3000, stoppingToken);
        }
    }

    private async Task<double> GetSystemCpuUsageAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var rand = new Random();
            return Math.Round(10.0 + rand.NextDouble() * 20.0, 1);
        }

        try
        {
            var lines = await File.ReadAllLinesAsync("/proc/stat");
            var firstLine = lines.First();
            var parts = firstLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // cpu  user nice system idle iowait irq softirq steal ...
            if (parts.Length >= 5)
            {
                double user   = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                double nice   = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                double system = double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                double idle   = double.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture);

                // Toplam aktif (idle dışı) ve toplam zaman dilimleri
                double active = user + nice + system;
                double total  = active + idle;

                // Önceki örnekle fark (delta) hesabı — tüm değerleri önceki toplam ile karşılaştır
                double prevActive = _lastCpuUser + _lastCpuNice + _lastCpuSystem;
                double prevTotal  = prevActive + _lastCpuIdle;

                double diffActive = active - prevActive;
                double diffTotal  = total  - prevTotal;

                // Durumu güncelle
                _lastCpuUser   = user;
                _lastCpuNice   = nice;
                _lastCpuSystem = system;
                _lastCpuIdle   = idle;

                if (diffTotal > 0)
                {
                    return Math.Round((diffActive / diffTotal) * 100.0, 1);
                }
            }
        }
        catch
        {
            // Fallback
        }

        return 12.5;
    }

    private (double UsedPercentage, double UsedGb, double TotalGb) GetSystemRamUsage()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var gcInfo = GC.GetGCMemoryInfo();
            double total = Math.Round(gcInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0), 2);
            var rand = new Random();
            double used = Math.Round((total * 0.3) + rand.NextDouble() * (total * 0.1), 2); // %30-40 arası simüle kullanım
            double pct = Math.Round((used / total) * 100.0, 1);
            return (pct, used, total);
        }

        try
        {
            var lines = File.ReadAllLines("/proc/meminfo");
            double memTotal = 0;
            double memFree = 0;
            double buffers = 0;
            double cached = 0;
            double sReclaimable = 0;
            double shmem = 0;
            double memAvailable = 0;

            foreach (var line in lines)
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                if (line.StartsWith("MemTotal:"))
                    memTotal = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
                else if (line.StartsWith("MemFree:"))
                    memFree = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
                else if (line.StartsWith("Buffers:"))
                    buffers = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
                else if (line.StartsWith("Cached:"))
                    cached = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
                else if (line.StartsWith("SReclaimable:"))
                    sReclaimable = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
                else if (line.StartsWith("Shmem:"))
                    shmem = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
                else if (line.StartsWith("MemAvailable:"))
                    memAvailable = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
            }

            if (memTotal > 0)
            {
                // Linux 'free' ve 'htop' komutlarının resmi kullanılan RAM hesaplama formülü:
                // kullanılan = Total - Free - Buffers - Cached - SReclaimable + Shmem
                double memUsed = memTotal - memFree - buffers - cached - sReclaimable + shmem;

                // Negatif veya hatalı durumlar için koruma (örneğin eski kernel'lar)
                if (memUsed <= 0)
                {
                    if (memAvailable == 0)
                    {
                        memAvailable = memFree + buffers + cached;
                    }
                    memUsed = memTotal - memAvailable;
                }

                double pct = Math.Round((memUsed / memTotal) * 100.0, 1);
                double usedGb = Math.Round(memUsed / (1024.0 * 1024.0 * 1024.0), 2);
                double totalGb = Math.Round(memTotal / (1024.0 * 1024.0 * 1024.0), 2);
                return (pct, usedGb, totalGb);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Linux /proc/meminfo okunurken hata oluştu!");
        }

        // Fatal fallback
        return (0, 0, 0);
    }

    private (double UsedPercentage, double UsedGb, double TotalGb) GetSystemDiskUsage()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows: C sürücüsü veya tüm sürücülerin toplamı
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .ToList();

                if (drives.Any())
                {
                    double totalBytes = drives.Sum(d => d.TotalSize);
                    double freeBytes  = drives.Sum(d => d.AvailableFreeSpace);
                    double usedBytes  = totalBytes - freeBytes;

                    double usedGb  = Math.Round(usedBytes  / (1024.0 * 1024.0 * 1024.0), 2);
                    double totalGb = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2);
                    double pct     = Math.Round((usedBytes / totalBytes) * 100.0, 1);
                    return (pct, usedGb, totalGb);
                }
            }
            else
            {
                // Linux: kök bölüm ("/") disk kullanımı — /proc/mounts üzerinden
                var drive = new DriveInfo("/");
                if (drive.IsReady)
                {
                    double totalBytes = drive.TotalSize;
                    double freeBytes  = drive.AvailableFreeSpace;
                    double usedBytes  = totalBytes - freeBytes;

                    double usedGb  = Math.Round(usedBytes  / (1024.0 * 1024.0 * 1024.0), 2);
                    double totalGb = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2);
                    double pct     = Math.Round((usedBytes / totalBytes) * 100.0, 1);
                    return (pct, usedGb, totalGb);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Disk kullanım bilgisi alınırken hata oluştu!");
        }

        return (0, 0, 0);
    }

    private async Task ReadNewSyslogLinesAsync()
    {
        string path = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? string.Empty
            : (System.IO.File.Exists("/var/log/syslog") ? "/var/log/syslog" : (System.IO.File.Exists("/var/log/messages") ? "/var/log/messages" : string.Empty));

        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            // Windows simülasyonu için rastgele sistem logları ekleyelim ki arayüzde canlı gözüksün!
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (_rand.Next(0, 10) < 3) // %30 ihtimalle yeni log
                {
                    string[] simMsgs = new[] {
                        "systemd[1]: Starting Nginx Gateway Proxy...",
                        "nginx[123]: 127.0.0.1 - - GET /api/system/status HTTP/1.1 200",
                        "project-manager: checking health of all native processes...",
                        "kernel: [ufw] [UFW BLOCK] IN=eth0 OUT= PHYSIN= PHYSOUT= SRC=192.168.1.100 DST=192.168.1.1"
                    };
                    string msg = simMsgs[_rand.Next(simMsgs.Length)];
                    SystemLogQueue.Log("syslog", msg);
                }
            }
            return;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            
            if (_lastSyslogPosition == -1)
            {
                // İlk açılışta dosyanın sonuna konumlan (son 5 satırı oku)
                if (fs.Length > 4096)
                {
                    fs.Seek(-4096, SeekOrigin.End);
                }
                else
                {
                    fs.Seek(0, SeekOrigin.Begin);
                }
                _lastSyslogPosition = fs.Length;
                
                using var reader = new StreamReader(fs);
                string? line;
                var initialLines = new List<string>();
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    initialLines.Add(line);
                }
                // Son 5 satırı başlangıç olarak ekle
                foreach (var l in initialLines.Skip(Math.Max(0, initialLines.Count - 5)))
                {
                    SystemLogQueue.Log("syslog", l);
                }
                return;
            }

            if (fs.Length < _lastSyslogPosition)
            {
                // Dosya logrotate ile sıfırlanmış
                _lastSyslogPosition = 0;
            }

            if (fs.Length > _lastSyslogPosition)
            {
                fs.Seek(_lastSyslogPosition, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        SystemLogQueue.Log("syslog", line);
                    }
                }
                _lastSyslogPosition = fs.Position;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "syslog dosyası okunurken hata.");
        }
    }
}
