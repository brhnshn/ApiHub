using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/audit-logs")]
public class AuditLogController : ControllerBase
{
    private readonly DockerPanelDbContext _dbContext;

    public AuditLogController(DockerPanelDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    private Guid GetUserId()
    {
        var idStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }

    private bool IsAdmin()
    {
        return User.IsInRole(UserRole.Administrator.ToString());
    }

    [HttpGet]
    public async Task<IActionResult> GetAuditLogs(
        [FromQuery] string? actionType = null, 
        [FromQuery] string? entityType = null, 
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 50)
    {
        var userId = GetUserId();
        var query = _dbContext.AuditLogs.Include(a => a.User).AsQueryable();

        if (!IsAdmin())
        {
            query = query.Where(a => a.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(actionType))
        {
            query = query.Where(a => a.Action == actionType);
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(a => a.TargetEntity == entityType);
        }

        var totalItems = await query.CountAsync();
        var logs = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.UserId,
                Username = a.User != null ? a.User.Username : "System",
                a.Action,
                a.TargetEntity,
                a.TargetId,
                a.Details,
                a.IpAddress,
                a.UserAgent,
                a.Timestamp
            })
            .ToListAsync();

        return Ok(new
        {
            TotalItems = totalItems,
            Page = page,
            PageSize = pageSize,
            Items = logs
        });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetAuditStats()
    {
        var userId = GetUserId();
        var query = _dbContext.AuditLogs.AsQueryable();

        if (!IsAdmin())
        {
            query = query.Where(a => a.UserId == userId);
        }

        var todayDate = DateTimeOffset.UtcNow.Date;
        var today = new DateTimeOffset(todayDate, TimeSpan.Zero);
        var totalToday = await query.CountAsync(a => a.Timestamp >= today);

        var popularActions = await query
            .GroupBy(a => a.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(5)
            .ToListAsync();

        return Ok(new
        {
            TotalToday = totalToday,
            PopularActions = popularActions
        });
    }
}
