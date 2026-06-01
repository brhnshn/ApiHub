using System;

namespace DockerPanel.Domain.Entities;

public class PushNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? DeepLink { get; set; }
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsRead { get; set; } = false;

    // Navigation
    public User? User { get; set; }
}
