using System.Threading;
using System.Threading.Tasks;
using DockerPanel.Client.Security;
using Microsoft.Maui.Storage;

namespace DockerPanel.Mobile.Security;

public sealed class MobileAuthTokenStore : IAuthTokenStore
{
    private const string TokenKey = "authToken";

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(TokenKey);
        }
        catch
        {
            return Preferences.Default.Get<string?>(TokenKey, null);
        }
    }

    public async Task SetTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            await SecureStorage.Default.SetAsync(TokenKey, token);
            Preferences.Default.Remove(TokenKey);
        }
        catch
        {
            Preferences.Default.Set(TokenKey, token);
        }
    }

    public Task RemoveTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            SecureStorage.Default.Remove(TokenKey);
        }
        catch
        {
            // SecureStorage can fail when Android keystore state changes.
        }

        Preferences.Default.Remove(TokenKey);
        return Task.CompletedTask;
    }
}
