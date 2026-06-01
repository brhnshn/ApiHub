using System;
using System.Threading.Tasks;

namespace DockerPanel.Domain.Interfaces;

public interface IAuditLogService
{
    Task LogAsync(Guid userId, string action, string targetEntity, Guid? targetId, string details, string ipAddress, string userAgent);
}
