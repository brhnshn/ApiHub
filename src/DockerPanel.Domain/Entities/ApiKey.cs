using System;

namespace DockerPanel.Domain.Entities;

public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty; // SHA-256 Hashed representation
    public string MaskedKey { get; set; } = string.Empty; // e.g., "dp_xyz...7890"
    public string EncryptedKey { get; set; } = string.Empty; // AES encrypted key
    public Guid UserId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    // Navigation Property
    public virtual User User { get; set; } = null!;
}
