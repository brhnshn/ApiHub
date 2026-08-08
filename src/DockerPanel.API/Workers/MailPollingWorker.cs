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
using MimeKit;

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
                    // EML dosyasını MimeKit ile düzgün UTF-8 formatında oku
                    var mimeMessage = await MimeMessage.LoadAsync(file);
                    var subject = mimeMessage.Subject ?? "Yeni İleti";
                    var sender = mimeMessage.From.FirstOrDefault()?.ToString() ?? "Bilinmeyen Gönderici";
                    var bodyText = mimeMessage.TextBody ?? "Bu e-postada metin içeriği bulunmuyor.";

                    // 1. FCM Push Bildirimi (UTF-8 destekli doğru konu başlığı ile)
                    await pushService.SendNotificationToUserAsync(
                        account.UserId, 
                        $"📧 Yeni E-posta: {subject}", 
                        $"{sender} kişisinden yeni bir ileti geldi.", 
                        "apihub://navigate?path=/webmail");

                    // 2. Yönlendirme (Forwarding) - Özel Format
                    if (account.ForwardingEnabled && !string.IsNullOrEmpty(account.ForwardingAddress))
                    {
                        _logger.LogInformation($"[Forwarding] {account.EmailAddress} adresinden {account.ForwardingAddress} adresine yönlendirme yapılıyor.");
                        
                        var forwardSubject = $"İletildi: {subject}";
                        var forwardBody = $@"Bu e-posta sisteminizdeki {account.EmailAddress} adresine gelmiş olup otomatik olarak size yönlendirilmiştir.

--- ORİJİNAL MESAJ BİLGİLERİ ---
Kimden: {sender}
Tarih: {mimeMessage.Date.ToString("dd.MM.yyyy HH:mm")}
Konu: {subject}

{bodyText}
";
                        await mailService.SendMailAsync(account.EmailAddress, account.DisplayName, account.ForwardingAddress, forwardSubject, forwardBody);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[MailPollingWorker] Dosya islenirken hata ({file}): {ex.Message}");
                }
            }
        }
    }
}
