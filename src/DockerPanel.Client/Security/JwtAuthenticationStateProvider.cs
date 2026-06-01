using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;

namespace DockerPanel.Client.Security;

public class JwtAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IAuthTokenStore _tokenStore;
    private readonly ClaimsPrincipal _anonymous = new(new ClaimsIdentity());

    public JwtAuthenticationStateProvider(IAuthTokenStore tokenStore)
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

            // Token'ın süresinin dolup dolmadığını doğrula
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

    public virtual async Task LoginAsync(string token)
    {
        await _tokenStore.SetTokenAsync(token);
        var identity = new ClaimsIdentity(ParseClaimsFromJwt(token), "jwt");
        var user = new ClaimsPrincipal(identity);
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(user)));
    }

    public virtual async Task LogoutAsync()
    {
        await _tokenStore.RemoveTokenAsync();
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_anonymous)));
    }

    protected IEnumerable<Claim> ParseClaimsFromJwt(string jwt)
    {
        var claims = new List<Claim>();
        var payload = jwt.Split('.')[1];

        var jsonBytes = ParseBase64WithoutPadding(payload);
        var keyValuePairs = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonBytes);

        if (keyValuePairs != null)
        {
            // Roller dizisini veya tekil rolü ayrıştır
            string[] roleKeys = new[] { "role", "roles", ClaimTypes.Role };
            object? rolesObj = null;
            string matchedKey = string.Empty;
            foreach (var key in roleKeys)
            {
                if (keyValuePairs.TryGetValue(key, out rolesObj))
                {
                    matchedKey = key;
                    break;
                }
            }

            if (rolesObj != null)
            {
                var rolesStr = rolesObj.ToString()?.Trim();
                if (rolesStr != null)
                {
                    if (rolesStr.StartsWith("[") && rolesStr.EndsWith("]"))
                    {
                        try
                        {
                            var parsedRoles = JsonSerializer.Deserialize<string[]>(rolesStr);
                            if (parsedRoles != null)
                            {
                                claims.AddRange(parsedRoles.Select(role => new Claim(ClaimTypes.Role, role)));
                            }
                        }
                        catch
                        {
                            claims.Add(new Claim(ClaimTypes.Role, rolesStr));
                        }
                    }
                    else
                    {
                        claims.Add(new Claim(ClaimTypes.Role, rolesStr));
                    }
                }
                keyValuePairs.Remove(matchedKey);
            }

            // Diğer claim'leri standart olarak ekle
            foreach (var kvp in keyValuePairs)
            {
                var val = kvp.Value.ToString();
                if (val != null)
                {
                    if (kvp.Key == "unique_name" || kvp.Key == "name")
                    {
                        claims.Add(new Claim(ClaimTypes.Name, val));
                    }
                    else if (kvp.Key == "nameid" || kvp.Key == "sub")
                    {
                        claims.Add(new Claim(ClaimTypes.NameIdentifier, val));
                    }
                    else if (kvp.Key == "role" || kvp.Key == "roles")
                    {
                        claims.Add(new Claim(ClaimTypes.Role, val));
                    }
                    else
                    {
                        claims.Add(new Claim(kvp.Key, val));
                    }
                }
            }
        }

        return claims;
    }

    protected byte[] ParseBase64WithoutPadding(string base64)
    {
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }
}
