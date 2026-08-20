using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DockerPanel.Domain.Interfaces;

namespace DockerPanel.API.Workers;

public class BackupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackupWorker> _logger;
    private DateTime? _lastBackupDate;

    public BackupWorker(IServiceScopeFactory scopeFactory, ILogger<BackupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackupWorker otomatik haftalık yedekleme servisi başlatıldı (Her Pazar 03:00 kontrolü).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;

                // Her Pazar günü saat 03:00'te ve o gün henüz yedek alınmadıysa çalıştır (Haftada 1 kez)
                if (now.DayOfWeek == DayOfWeek.Sunday && now.Hour == 3 && (_lastBackupDate == null || _lastBackupDate.Value.Date != now.Date))
                {
                    _logger.LogInformation("Pazar saat 03:00. Otomatik haftalık yedekleme tetikleniyor...");
                    
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var backupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
                        // Sistem / Otomatik yedekler için default/admin Guid veya Guid.Empty
                        await backupService.TriggerBackupAsync(Guid.Empty);
                    }

                    _lastBackupDate = now;
                    _logger.LogInformation("Otomatik haftalık yedekleme tamamlandı.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Otomatik haftalık yedekleme işlemi sırasında bir hata oluştu.");
            }

            // Her 1 saatte bir kontrol et
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
