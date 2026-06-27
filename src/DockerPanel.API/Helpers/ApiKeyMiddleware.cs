using System;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Helpers;

public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, DockerPanelDbContext dbContext)
    {
        if (context.Request.Headers.TryGetValue("X-API-Key", out var extractedApiKey))
        {
            var apiKeyString = extractedApiKey.ToString().Trim();
            if (string.IsNullOrWhiteSpace(apiKeyString))
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { Message = "API Anahtarı bos olamaz!" });
                return;
            }

            // SHA256 Hash
            using var sha256 = SHA256.Create();
            var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(apiKeyString));
            var hashedKey = Convert.ToHexString(hashedBytes).ToLower();

            var apiKeyEntity = await dbContext.ApiKeys
                .Include(k => k.User)
                .FirstOrDefaultAsync(k => k.KeyHash == hashedKey);

            if (apiKeyEntity == null || !apiKeyEntity.IsActive)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { Message = "Gecersiz veya aktif olmayan API anahtari!" });
                return;
            }

            if (apiKeyEntity.ExpiresAt.HasValue && apiKeyEntity.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { Message = "API anahtarinin suresi dolmus!" });
                return;
            }

            // Create User Claims
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, apiKeyEntity.User.Id.ToString()),
                new Claim(ClaimTypes.Name, apiKeyEntity.User.Username),
                new Claim(ClaimTypes.Role, apiKeyEntity.User.Role.ToString()),
                new Claim("ApiKeyId", apiKeyEntity.Id.ToString())
            };

            var identity = new ClaimsIdentity(claims, "ApiKey");
            context.User = new ClaimsPrincipal(identity);

            // Update LastUsedAt
            apiKeyEntity.LastUsedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
        }

        await _next(context);
    }
}
