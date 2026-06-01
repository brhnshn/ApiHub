using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Maui.Devices;

namespace DockerPanel.Mobile.Services;

/// <summary>
/// FCM token'ı sunucu API'sine kaydeden servis.
/// OnNewToken tetiklendiğinde veya uygulama açılışında çağrılır.
/// </summary>
public class PushTokenRegistrationService
{
    private readonly SecureTokenService _secureTokenService;
    private readonly HttpClient _httpClient;

    public PushTokenRegistrationService(
        SecureTokenService secureTokenService,
        HttpClient httpClient)
    {
        _secureTokenService = secureTokenService;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Verilen token'ı sunucuya kayıt eder.
    /// Ağ hatası veya auth hatası uygulama akışını kesmez.
    /// </summary>
    public async Task RegisterTokenWithServerAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        // Token'ı önce güvenli depolamaya yaz
        await _secureTokenService.SaveTokenAsync(token);

        try
        {
            var deviceName = GetDeviceName();
            var payload = new
            {
                token = token,
                platform = "Android",
                deviceName = deviceName
            };

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _httpClient.PostAsJsonAsync("api/devices/register", payload, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[FCM] Token sunucuya başarıyla kaydedildi: {deviceName}");
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[FCM] Token kayıt hatası: HTTP {(int)response.StatusCode} - {errorBody}");
            }
        }
        catch (Exception ex)
        {
            // Ağ hatası vs. uygulama akışını bloke etmemeli
            System.Diagnostics.Debug.WriteLine($"[FCM] Token API kayıt exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Uygulama açılışında mevcut token'ı (varsa) sunucuya yeniden kaydeder.
    /// Token zaten sunucudaysa API 200 döner (idempotent upsert).
    /// </summary>
    public async Task RefreshAndRegisterStoredTokenAsync()
    {
        var storedToken = await _secureTokenService.GetTokenAsync();
        if (!string.IsNullOrWhiteSpace(storedToken))
        {
            await RegisterTokenWithServerAsync(storedToken);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[FCM] Depolamada kayıtlı FCM token bulunamadı. Firebase'den yeni token bekleniyor...");
        }
    }

    private static string GetDeviceName()
    {
        try
        {
            return $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim();
        }
        catch
        {
            return "Android Cihaz";
        }
    }
}
