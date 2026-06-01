using System;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace DockerPanel.Mobile.Services;

public class SecureTokenService
{
    private const string FcmTokenKey = "fcmToken";

    public async Task SaveTokenAsync(string token)
    {
        try
        {
            await SecureStorage.Default.SetAsync(FcmTokenKey, token);
            Preferences.Default.Remove(FcmTokenKey);
        }
        catch
        {
            Preferences.Default.Set(FcmTokenKey, token);
        }
    }

    public async Task<string?> GetTokenAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(FcmTokenKey);
        }
        catch
        {
            return Preferences.Default.Get<string?>(FcmTokenKey, null);
        }
    }

    public void RemoveToken()
    {
        try
        {
            SecureStorage.Default.Remove(FcmTokenKey);
        }
        catch
        {
            // SecureStorage can fail when Android keystore state changes.
        }

        Preferences.Default.Remove(FcmTokenKey);
    }
}
