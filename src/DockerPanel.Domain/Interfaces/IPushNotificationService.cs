using System;
using System.Threading.Tasks;

namespace DockerPanel.Domain.Interfaces;

public interface IPushNotificationService
{
    Task SendNotificationToUserAsync(Guid userId, string title, string body, string? deepLink = null);
    bool IsFcmConfigured();
}
