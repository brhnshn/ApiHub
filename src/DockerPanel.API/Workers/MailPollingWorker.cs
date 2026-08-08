using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DockerPanel.API.Workers;

public class MailPollingWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MailPollingWorker> _logger;
    private DateTimeOffset _lastCheckTime = DateTimeOffset.UtcNow;

    public MailPollingWorker(IServiceProvider serviceProvider, ILogger<MailPollingWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MailPollingWorker baslatildi.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckNewMailsAsync();
                _lastCheckTime = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MailPollingWorker calisirken hata olustu.");
            }

            // Her 60 saniyede bir calis
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private async Task CheckNewMailsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DockerPanelDbContext>();
        var mailService = scope.ServiceProvider.GetRequiredService<IMailService>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        var accounts = await dbContext.MailAccounts.AsNoTracking().ToListAsync();

        foreach (var account in accounts)
        {
            var parts = account.EmailAddress.Split('@');
            if (parts.Length != 2) continue;

            var username = parts[0];
            var domain = parts[1];

            // Maildir / new dizini yolu
            var basePath = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                ? Path.Combine(AppContext.BaseDirectory, "opt_dockerpanel", "mail", "data")
                : "/opt/dockerpanel/mail/data";

            var mailFolder = Path.Combine(basePath, domain, username);
            
            // Maildir tespiti
            string newDir;
            if (Directory.Exists(Path.Combine(mailFolder, "new")))
            {
                newDir = Path.Combine(mailFolder, "new");
            }
            else if (Directory.Exists(Path.Combine(mailFolder, "Maildir", "new")))
            {
                newDir = Path.Combine(mailFolder, "Maildir", "new");
            }
            else
            {
                newDir = Path.Combine(mailFolder, "new");
            }

            if (!Directory.Exists(newDir)) continue;

            var newFiles = Directory.GetFiles(newDir)
                .Where(f => File.GetLastWriteTimeUtc(f) > _lastCheckTime.UtcDateTime)
                .ToList();

            foreach (var file in newFiles)
            {
                try
                {
                    // Dosyayı oku ve konu vs. al (isteğe bağlı)
                    // Hızlı bildirim için parsing kısmını basitleştirebiliriz.
                    var content = await File.ReadAllTextAsync(file);
                    var subjectLine = content.Split('\n').FirstOrDefault(l => l.StartsWith("Subject:"));
                    var subject = subjectLine?.Replace("Subject:", "").Trim() ?? "Yeni İleti";

                    // 1. FCM Push
                    await pushService.SendNotificationToUserAsync(
                        account.UserId, 
                        $"📧 Yeni E-posta: {subject}", 
                        $"{account.EmailAddress} adresine yeni bir ileti geldi.", 
                        "apihub://navigate?path=/webmail");

                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[MailPollingWorker] Dosya islenirken hata ({file}): {ex.Message}");
                }
            }
        }
    }
}
