using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/devices")]
public class DevicesController : ControllerBase
{
    private readonly DockerPanelDbContext _dbContext;

    public DevicesController(DockerPanelDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    private Guid GetUserId()
    {
        var idStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }

    [HttpPost("register")]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { Message = "Cihaz token'ı boş olamaz!" });
        }

        var userId = GetUserId();

        // Token mükerrerlik denetimi
        var existing = await _dbContext.DeviceTokens.FirstOrDefaultAsync(d => d.Token == request.Token);
        if (existing != null)
        {
            existing.UserId = userId;
            existing.LastUsedAt = DateTimeOffset.UtcNow;
            existing.DeviceName = request.DeviceName ?? existing.DeviceName;
            existing.Platform = "Android";
            _dbContext.Entry(existing).State = EntityState.Modified;
        }
        else
        {
            var deviceToken = new DeviceToken
            {
                UserId = userId,
                Token = request.Token,
                Platform = "Android",
                DeviceName = request.DeviceName ?? "Bilinmeyen Cihaz",
                CreatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = DateTimeOffset.UtcNow
            };
            _dbContext.DeviceTokens.Add(deviceToken);
        }

        await _dbContext.SaveChangesAsync();
        return Ok(new { Message = "Cihaz token'ı başarıyla kaydedildi." });
    }

    [HttpDelete("unregister/{token}")]
    public async Task<IActionResult> UnregisterDevice(string token)
    {
        var deviceToken = await _dbContext.DeviceTokens.FirstOrDefaultAsync(d => d.Token == token);
        if (deviceToken == null) return NotFound();

        // Check ownership
        if (deviceToken.UserId != GetUserId()) return Forbid();

        _dbContext.DeviceTokens.Remove(deviceToken);
        await _dbContext.SaveChangesAsync();

        return Ok(new { Message = "Cihaz token'ı başarıyla silindi." });
    }

    [HttpGet]
    public async Task<IActionResult> GetDevices()
    {
        var userId = GetUserId();
        var devices = await _dbContext.DeviceTokens
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastUsedAt)
            .Select(d => new DeviceResponseDto
            {
                Token = d.Token,
                Platform = d.Platform,
                DeviceName = d.DeviceName,
                CreatedAt = d.CreatedAt,
                LastUsedAt = d.LastUsedAt,
                IsActive = d.LastUsedAt >= DateTimeOffset.UtcNow.AddMinutes(-30)
            })
            .ToListAsync();

        return Ok(devices);
    }

    [HttpPost("test-notification")]
    public async Task<IActionResult> SendTestNotification([FromQuery] string token, [FromServices] IPushNotificationService pushService)
    {
        if (string.IsNullOrWhiteSpace(token)) return BadRequest(new { Message = "Token boş olamaz!" });
        
        var deviceToken = await _dbContext.DeviceTokens.FirstOrDefaultAsync(d => d.Token == token);
        if (deviceToken == null)
        {
            return NotFound(new
            {
                Message = "Bu token ile kayıtlı cihaz bulunamadı! Mobil uygulamayı açıp tekrar giriş yapın.",
                Hint = "Uygulamayı kapatıp açmak token'ı yeniden sunucuya kaydeder.",
                TokenPrefix = token.Length > 20 ? token[..20] + "..." : token
            });
        }

        if (deviceToken.UserId != GetUserId() && !User.IsInRole(UserRole.Administrator.ToString()))
        {
            return Forbid();
        }

        await pushService.SendNotificationToUserAsync(
            deviceToken.UserId,
            "🔔 ApiHub Deneme Bildirimi",
            $"Bu bildirim '{deviceToken.DeviceName}' cihazını test etmek için panel üzerinden gönderilmiştir!",
            "apihub://navigate?path=/containers");

        return Ok(new { Message = $"Test bildirimi '{deviceToken.DeviceName}' cihazına gönderildi." });
    }
}

public class DeviceResponseDto
{
    public string Token { get; set; } = string.Empty;
    public string Platform { get; set; } = "Android";
    public string DeviceName { get; set; } = "Bilinmeyen Cihaz";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public bool IsActive { get; set; }
}

public class RegisterDeviceRequest
{
    public string Token { get; set; } = string.Empty;
    public string? Platform { get; set; } = "Android";
    public string? DeviceName { get; set; } = "Bilinmeyen Cihaz";
}
