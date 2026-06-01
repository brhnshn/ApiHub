using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using DockerPanel.Client.Security;

namespace DockerPanel.Mobile.Security;

public class MobileJwtAuthenticationStateProvider : JwtAuthenticationStateProvider
{
    private readonly ClaimsPrincipal _anonymous = new(new ClaimsIdentity());

    private readonly IAuthTokenStore _tokenStore;

    public MobileJwtAuthenticationStateProvider(IAuthTokenStore tokenStore) : base(tokenStore)
    {
        _tokenStore = tokenStore;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var token = await _tokenStore.GetTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new AuthenticationState(_anonymous);
            }

            var identity = new ClaimsIdentity(ParseClaimsFromJwt(token), "jwt");
            var user = new ClaimsPrincipal(identity);

            // Verify expiration
            var expClaim = identity.FindFirst("exp");
            if (expClaim != null && long.TryParse(expClaim.Value, out var expUnix))
            {
                var expirationTime = DateTimeOffset.FromUnixTimeSeconds(expUnix);
                if (expirationTime <= DateTimeOffset.UtcNow)
                {
                    await LogoutAsync();
                    return new AuthenticationState(_anonymous);
                }
            }

            return new AuthenticationState(user);
        }
        catch
        {
            return new AuthenticationState(_anonymous);
        }
    }

    public override async Task LoginAsync(string token)
    {
        await _tokenStore.SetTokenAsync(token);
        var identity = new ClaimsIdentity(ParseClaimsFromJwt(token), "jwt");
        var user = new ClaimsPrincipal(identity);
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(user)));
    }

    public override async Task LogoutAsync()
    {
        await _tokenStore.RemoveTokenAsync();
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_anonymous)));
    }
}
