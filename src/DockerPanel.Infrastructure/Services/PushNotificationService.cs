using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Entities;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.Infrastructure.Services;

public class PushNotificationService : IPushNotificationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private static readonly object _initLock = new();

    public PushNotificationService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    private void EnsureFirebaseInitialized()
    {
        if (FirebaseApp.DefaultInstance != null) return;
        lock (_initLock)
        {
            if (FirebaseApp.DefaultInstance != null) return;

            // Search in typical execution paths
            var path = "firebase-service-account.json";
            if (!System.IO.File.Exists(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, "firebase-service-account.json");
            }
            if (!System.IO.File.Exists(path))
            {
                path = Path.Combine(Directory.GetCurrentDirectory(), "firebase-service-account.json");
            }
            if (!System.IO.File.Exists(path))
            {
                // Sibling repository root search
                path = Path.Combine(Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? "", "firebase-service-account.json");
            }
            if (!System.IO.File.Exists(path))
            {
                // Check if in solution root but looking inside project folder
                path = Path.Combine(Directory.GetCurrentDirectory(), "src", "DockerPanel.API", "firebase-service-account.json");
            }
            if (!System.IO.File.Exists(path))
            {
                // Safe deployment-persistent path check (recommended)
                path = "/opt/dockerpanel/api/firebase-service-account.json";
            }
            if (!System.IO.File.Exists(path))
            {
                // Direct production server deployment path check
                path = "/opt/dockerpanel/api/DockerPanel_V1/firebase-service-account.json";
            }

            if (System.IO.File.Exists(path))
            {
                try
                {
                    FirebaseApp.Create(new AppOptions()
                    {
                        Credential = GoogleCredential.FromFile(path)
                    });
                    SystemLogQueue.Log("info", "[FCM v1] FirebaseAdmin SDK başarıyla hizmet hesabı dosyası ile başlatıldı.");
                }
                catch (Exception ex)
                {
                    SystemLogQueue.Log("error", $"[FCM v1] FirebaseAdmin başlatma hatası: {ex.Message}");
                }
            }
            else
            {
                SystemLogQueue.Log("warning", "[FCM v1] firebase-service-account.json dosyası bulunamadı! Simülasyon modunda çalışılacak.");
            }
        }
    }

    public async Task SendNotificationToUserAsync(Guid userId, string title, string body, string? deepLink = null)
    {
        SystemLogQueue.Log("info", $"[FCM v1] Bildirim gönderimi tetiklendi. Başlık: '{title}', Gövde: '{body}', Link: '{deepLink ?? "Yok"}'");

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DockerPanelDbContext>();

        // Kullanıcının kayıtlı cihaz token'larını getir
        var devices = dbContext.DeviceTokens
            .Where(d => d.UserId == userId || userId == Guid.Empty) // Guid.Empty ise tüm cihazlara at
            .ToList();

        if (!devices.Any())
        {
            SystemLogQueue.Log("info", "[FCM v1] Kayıtlı mobil cihaz bulunmadığı için push gönderilmedi.");
            return;
        }

        // FirebaseAdmin başlatıldığından emin ol
        EnsureFirebaseInitialized();

        if (FirebaseApp.DefaultInstance == null)
        {
            // Firebase konfigüre edilmemişse veya dosya yoksa simülasyon logu bas
            SystemLogQueue.Log("info", $"[FCM v1 Simülasyonu] Otomatik bildirim tüm cihazlara başarıyla iletildi: [{title}] -> {body}");
            // Yine de DB'ye kaydet
            var simRecord = new PushNotification
            {
                UserId = userId,
                Title = title,
                Body = body,
                DeepLink = deepLink,
                SentAt = DateTimeOffset.UtcNow,
                IsRead = false
            };
            dbContext.PushNotifications.Add(simRecord);
            await dbContext.SaveChangesAsync();
            return;
        }

        foreach (var dev in devices)
        {
            try
            {
                var message = new Message()
                {
                    Token = dev.Token,
                    Notification = new Notification()
                    {
                        Title = title,
                        Body = body
                    },
                    Android = new AndroidConfig()
                    {
                        Priority = Priority.High,
                        Notification = new AndroidNotification()
                        {
                            Sound = "default",
                            ChannelId = "apihub_notifications"
                        }
                    },
                    Data = new Dictionary<string, string>()
                    {
                        { "deepLink", deepLink ?? "apihub://navigate?path=/containers" }
                    }
                };

                var response = await FirebaseMessaging.DefaultInstance.SendAsync(message);

                // Bildirimi DB'ye kaydet (tarihçe için)
                var pushRecord = new PushNotification
                {
                    UserId = userId,
                    Title = title,
                    Body = body,
                    DeepLink = deepLink,
                    SentAt = DateTimeOffset.UtcNow,
                    IsRead = false
                };
                dbContext.PushNotifications.Add(pushRecord);

                dev.LastUsedAt = DateTimeOffset.UtcNow;
                dbContext.Entry(dev).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
                SystemLogQueue.Log("info", $"[FCM v1] Bildirim başarıyla cihazına ulaştı: {dev.DeviceName} (API ID: {response})");
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("error", $"[FCM v1 Hatası] İletim hatası ({dev.DeviceName}): {ex.Message}");
            }
        }

        await dbContext.SaveChangesAsync();
    }

    public bool IsFcmConfigured()
    {
        EnsureFirebaseInitialized();
        return FirebaseApp.DefaultInstance != null;
    }
}
