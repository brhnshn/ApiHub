using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Domain.Entities;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly DockerPanelDbContext _dbContext;

    public NotificationsController(DockerPanelDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    private Guid GetUserId()
    {
        var idStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }

    /// <summary>
    /// Kullanıcının son 50 bildirimini getirir.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetNotifications([FromQuery] int take = 50)
    {
        var userId = GetUserId();
        var notifications = await _dbContext.PushNotifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.SentAt)
            .Take(Math.Min(take, 100))
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                Title = n.Title,
                Body = n.Body,
                DeepLink = n.DeepLink,
                SentAt = n.SentAt,
                IsRead = n.IsRead
            })
            .ToListAsync();

        return Ok(notifications);
    }

    /// <summary>
    /// Okunmamış bildirim sayısını getirir.
    /// </summary>
    [HttpGet("unread-count")]
    public async Task<IActionResult> GetUnreadCount()
    {
        var userId = GetUserId();
        var count = await _dbContext.PushNotifications
            .CountAsync(n => n.UserId == userId && !n.IsRead);
        return Ok(new { Count = count });
    }

    /// <summary>
    /// Tüm bildirimleri okunmuş olarak işaretler.
    /// </summary>
    [HttpPost("mark-all-read")]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = GetUserId();
        var unread = await _dbContext.PushNotifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ToListAsync();

        foreach (var n in unread)
            n.IsRead = true;

        await _dbContext.SaveChangesAsync();
        return Ok(new { Message = $"{unread.Count} bildirim okundu olarak işaretlendi." });
    }

    /// <summary>
    /// Tek bir bildirimi okunmuş işaretler.
    /// </summary>
    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var userId = GetUserId();
        var notification = await _dbContext.PushNotifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

        if (notification == null) return NotFound();
        notification.IsRead = true;
        await _dbContext.SaveChangesAsync();
        return Ok();
    }

    /// <summary>
    /// Tüm bildirimleri siler.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> ClearAll()
    {
        var userId = GetUserId();
        var all = await _dbContext.PushNotifications
            .Where(n => n.UserId == userId)
            .ToListAsync();
        _dbContext.PushNotifications.RemoveRange(all);
        await _dbContext.SaveChangesAsync();
        return Ok(new { Message = "Tüm bildirimler silindi." });
    }
}

public class NotificationDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? DeepLink { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public bool IsRead { get; set; }
}
