using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace DockerPanel.Client.Security;

public sealed class BrowserAuthTokenStore : IAuthTokenStore
{
    private readonly IJSRuntime _jsRuntime;

    public BrowserAuthTokenStore(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        return await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", cancellationToken, new object?[] { "authToken" });
    }

    public async Task SetTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", cancellationToken, new object?[] { "authToken", token });
    }

    public async Task RemoveTokenAsync(CancellationToken cancellationToken = default)
    {
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", cancellationToken, new object?[] { "authToken" });
    }
}
