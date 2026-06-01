using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;

namespace DockerPanel.Client.Security;

public class JwtAuthorizationHandler : DelegatingHandler
{
    private readonly IAuthTokenStore _tokenStore;
    private readonly NavigationManager _navigation;

    public JwtAuthorizationHandler(IAuthTokenStore tokenStore, NavigationManager navigation)
    {
        _tokenStore = tokenStore;
        _navigation = navigation;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var token = await _tokenStore.GetTokenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
        catch
        {
            // Startup/storage failures should not block anonymous requests.
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            try
            {
                // Use CancellationToken.None to ensure token removal completes fully
                // and isn't aborted when the current request is terminated/cancelled.
                await _tokenStore.RemoveTokenAsync(CancellationToken.None);
                
                var relativeUri = _navigation.ToBaseRelativePath(_navigation.Uri);
                // Only navigate if we aren't already on the login page to prevent recursive loops
                if (!relativeUri.StartsWith("login", StringComparison.OrdinalIgnoreCase))
                {
                    _navigation.NavigateTo("/login");
                }
            }
            catch
            {
                // Prevent infinite redirect loops or crashed HTTP calls if storage breaks
            }
        }

        return response;
    }
}
