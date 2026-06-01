using System;
using System.Threading.Tasks;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.Infrastructure.Services;

public class AuditLogService : IAuditLogService
{
    private readonly DockerPanelDbContext _context;

    public AuditLogService(DockerPanelDbContext context)
    {
        _context = context;
    }

    public async Task LogAsync(Guid userId, string action, string targetEntity, Guid? targetId, string details, string ipAddress, string userAgent)
    {
        try
        {
            var log = new AuditLog
            {
                UserId = userId,
                Action = action,
                TargetEntity = targetEntity,
                TargetId = targetId,
                Details = details ?? string.Empty,
                IpAddress = ipAddress ?? string.Empty,
                UserAgent = userAgent ?? string.Empty,
                Timestamp = DateTimeOffset.UtcNow
            };

            _context.AuditLogs.Add(log);
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AuditLog Error] Failed to log audit event: {ex.Message}");
            SystemLogQueue.Log("error", $"[AuditLog Error] Failed to log audit event: {ex.Message}");
        }
    }
}
