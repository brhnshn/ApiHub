using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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
    private readonly ISystemMetricsService _metricsService;
    private long _lastSyslogPosition = -1;
    // static: her iterasyonda new Random() üretilmesi önleniyor (seed sorunu + performans)
    private static readonly Random _rand = new();
    private int _watchdogCounter = 0;
    private int _periodicErrorRecoveryCounter = 0;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _watchdogFailures = new();
    private readonly DateTimeOffset _startedTime = DateTimeOffset.UtcNow;

    public MetricBackgroundWorker(
        IServiceScopeFactory scopeFactory,
        IHubContext<MetricLogHub> hubContext,
        ILogger<MetricBackgroundWorker> logger,
        ISystemMetricsService metricsService)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
        _metricsService = metricsService;

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
                var metrics = await _metricsService.GetCurrentMetricsAsync();

                // Tüm istemcilere genel sunucu metriklerini yayınla
                await _hubContext.Clients.All.SendAsync("ReceiveSystemMetrics", new
                {
                    Cpu = metrics.Cpu,
                    RamPercentage = metrics.RamPercentage,
                    RamUsedGb = metrics.RamUsedGb,
                    RamTotalGb = metrics.RamTotalGb,
                    DiskUsedPercentage = metrics.DiskUsedPercentage,
                    DiskUsedGb = metrics.DiskUsedGb,
                    DiskTotalGb = metrics.DiskTotalGb
                }, stoppingToken);

                // 2. Her bir aktif proje için donanım metriklerini al ve ilgili gruba yay
                _watchdogCounter = (_watchdogCounter + 1) % 5;
                bool runWatchdog = (_watchdogCounter == 0);

                _periodicErrorRecoveryCounter = (_periodicErrorRecoveryCounter + 1) % 100; // 100 * 3s = 300s = 5dk
                if (_periodicErrorRecoveryCounter == 0)
                {
                    try
                    {
                        using (var scope = _scopeFactory.CreateScope())
                        {
                            var dbContext = scope.ServiceProvider.GetRequiredService<DockerPanelDbContext>();
                            var errorProjects = await dbContext.Projects
                                .Where(p => p.Status == ProjectStatus.Error)
                                .ToListAsync(stoppingToken);

                            foreach (var project in errorProjects)
                            {
                                _logger.LogInformation("[Watchdog] Hata durumundaki '{ProjectName}' projesi için 5 dakikalık periyodik kurtarma tetiklendi.", project.Name);
                                SystemLogQueue.Log("info", $"[Watchdog] Hata durumundaki '{project.Name}' projesi için 5 dakikalık periyodik otomatik kurtarma başlatılıyor...");
                                
                                _watchdogFailures.TryRemove(project.Id, out _);
                                
                                project.Status = ProjectStatus.Running;
                                dbContext.Entry(project).Property(p => p.Status).IsModified = true;
                            }

                            if (errorProjects.Any())
                            {
                                await dbContext.SaveChangesAsync(stoppingToken);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Watchdog] Periyodik otomatik kurtarma işlemi sırasında hata oluştu.");
                    }
                }

                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<DockerPanelDbContext>();
                    var containerService = scope.ServiceProvider.GetRequiredService<IProjectContainerService>();
                    var processManagerService = scope.ServiceProvider.GetRequiredService<IProcessManagerService>();
                    var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

                    var activeProjects = dbContext.Projects
                        .AsNoTracking()
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
                                // Watchdog: Check if the Docker container is actually running (every 15s)
                                bool isRunning = true;
                                bool isTransitioning = ProcessTransitionTracker.IsTransitioning(project.Name);

                                if (runWatchdog && !isTransitioning)
                                {
                                    isRunning = await containerService.IsContainerRunningAsync(project.DockerContainerId);

                                    // Transient check verification to prevent false alarms
                                    if (!isRunning)
                                    {
                                        isRunning = await containerService.IsContainerRunningAsync(project.DockerContainerId);
                                    }

                                    if (!isRunning)
                                    {
                                            int failures = _watchdogFailures.AddOrUpdate(project.Id, 1, (key, val) => val + 1);
                                            _logger.LogWarning("[Watchdog] Docker container for project {ProjectName} ({ProjectId}) is not running! Failure count: {Failures}", project.Name, project.Id, failures);

                                            if (failures >= 3)
                                            {
                                                _logger.LogError("[Watchdog] Docker container for project {ProjectName} ({ProjectId}) failed to start after {Failures} attempts. Halted.", project.Name, project.Id, failures);
                                                SystemLogQueue.Log("error", $"[Watchdog] '{project.Name}' Docker projesi üst üste {failures} kez başlatılamadı. Otomatik kurtarma durduruldu, proje durumu 'Hata' olarak güncellendi.");

                                                await pushService.SendNotificationToUserAsync(
                                                    project.UserId,
                                                    "⚠️ Otomatik Kurtarma Başarısız",
                                                    $"'{project.Name}' Docker konteyner servisi sürekli çöküyor ve otomatik başlatılamadı. Servis durduruldu. Lütfen logları inceleyin.",
                                                    $"apihub://navigate?path=/containers&projectId={project.Id}");

                                                project.Status = ProjectStatus.Error;
                                                project.StartedAt = null;
                                                projectStateChanged = true;
                                                _watchdogFailures.TryRemove(project.Id, out _);
                                            }
                                            else
                                            {
                                                SystemLogQueue.Log("warning", $"[Watchdog] '{project.Name}' Docker projesi durmuş durumda tespit edildi (Kurtarma Denemesi {failures}/3), otomatik yeniden başlatılıyor...");
                                                
                                                // Send alert only on first detection to avoid spam
                                                if (failures == 1)
                                                {
                                                    await pushService.SendNotificationToUserAsync(
                                                        project.UserId,
                                                        "🔴 Servis Durdu (Docker)",
                                                        $"'{project.Name}' Docker konteyner servisi durmuş durumda tespit edildi, otomatik yeniden başlatılıyor...",
                                                        $"apihub://navigate?path=/containers&projectId={project.Id}");
                                                }

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
                                        }
                                        else
                                        {
                                            // Reset tracker if check passes
                                            _watchdogFailures.TryRemove(project.Id, out _);
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
                                // Watchdog: Check if the Native process is actually running (every 15s)
                                bool isRunning = true;
                                bool isTransitioning = ProcessTransitionTracker.IsTransitioning(project.Name);

                                if (runWatchdog && !isTransitioning)
                                {
                                    string runDir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                                        ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "project-manager")
                                        : "/run/project-manager";
                                    string pidFile = Path.Combine(runDir, $"{project.Name}.pid");
                                    bool pidFileExistsBeforeCheck = File.Exists(pidFile);

                                    isRunning = await processManagerService.IsProcessRunningAsync(project.Name);

                                    // Transient check verification to prevent false alarms
                                    if (!isRunning)
                                    {
                                        isRunning = await processManagerService.IsProcessRunningAsync(project.Name);
                                    }

                                    if (!isRunning)
                                    {
                                            bool isStartupGracePeriod = (DateTimeOffset.UtcNow - _startedTime).TotalMinutes < 2;
                                            if (!isStartupGracePeriod && !pidFileExistsBeforeCheck && !_watchdogFailures.ContainsKey(project.Id))
                                            {
                                                _logger.LogInformation("[Watchdog] Native process {ProjectName} ({ProjectId}) is not running and PID file did not exist (clean CLI stop). Setting status to Stopped.", project.Name, project.Id);
                                                project.Status = ProjectStatus.Stopped;
                                                project.StartedAt = null;
                                                projectStateChanged = true;
                                            }
                                            else
                                            {
                                                int failures = _watchdogFailures.AddOrUpdate(project.Id, 1, (key, val) => val + 1);
                                                _logger.LogWarning("[Watchdog] Native process for project {ProjectName} ({ProjectId}) is not running! Failure count: {Failures}", project.Name, project.Id, failures);

                                                if (failures >= 3)
                                                {
                                                    _logger.LogError("[Watchdog] Native process for project {ProjectName} ({ProjectId}) failed to start after {Failures} attempts. Halted.", project.Name, project.Id, failures);
                                                    SystemLogQueue.Log("error", $"[Watchdog] '{project.Name}' Native projesi üst üste {failures} kez başlatılamadı. Otomatik kurtarma durduruldu, proje durumu 'Hata' olarak güncellendi.");

                                                    await pushService.SendNotificationToUserAsync(
                                                        project.UserId,
                                                        "⚠️ Otomatik Kurtarma Başarısız",
                                                        $"'{project.Name}' native süreci sürekli çöküyor ve otomatik başlatılamadı. Süreç durduruldu. Lütfen logları inceleyin.",
                                                        $"apihub://navigate?path=/containers&projectId={project.Id}");

                                                    project.Status = ProjectStatus.Error;
                                                    project.StartedAt = null;
                                                    projectStateChanged = true;
                                                    _watchdogFailures.TryRemove(project.Id, out _);
                                                }
                                                else
                                                {
                                                    SystemLogQueue.Log("warning", $"[Watchdog] '{project.Name}' Native projesi durmuş durumda tespit edildi (Kurtarma Denemesi {failures}/3), otomatik yeniden başlatılıyor...");
                                                    
                                                    // Send alert only on first detection to avoid spam
                                                    if (failures == 1)
                                                    {
                                                        await pushService.SendNotificationToUserAsync(
                                                            project.UserId,
                                                            "🔴 Servis Durdu (Native)",
                                                            $"'{project.Name}' native süreci durmuş durumda tespit edildi, otomatik yeniden başlatılıyor...",
                                                            $"apihub://navigate?path=/containers&projectId={project.Id}");
                                                    }

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
                                            }
                                        }
                                        else
                                        {
                                            // Reset tracker if check passes
                                            _watchdogFailures.TryRemove(project.Id, out _);
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
                                var dbProject = await dbContext.Projects.FirstOrDefaultAsync(p => p.Id == project.Id, CancellationToken.None);
                                if (dbProject != null)
                                {
                                    dbProject.Status = project.Status;
                                    dbProject.StartedAt = project.StartedAt;
                                    await dbContext.SaveChangesAsync(CancellationToken.None);
                                }
                            }

                            // Tüm istemcilere ve ilgili proje SignalR grubuna canlı metrikleri bas
                            var metricPayload = new
                            {
                                ProjectId = project.Id,
                                Cpu = cpu,
                                RamBytes = (long)ramBytes,
                                RamLimitBytes = (long)ramLimitBytes,
                                RamPercentage = ramPercentage
                            };

                            await _hubContext.Clients.All.SendAsync("ReceiveProjectMetrics", metricPayload, stoppingToken);
                            await _hubContext.Clients.Group($"project_{project.Id}").SendAsync("ReceiveProjectMetrics", metricPayload, stoppingToken);

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
