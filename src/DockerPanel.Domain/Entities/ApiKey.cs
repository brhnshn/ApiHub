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

    // Integration Revision Fields
    public string? Provider { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? BaseUrl { get; set; }
    public string? DefaultModel { get; set; }
    public string? LastModifiedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    // Usage Statistics
    public int TotalRequests { get; set; }
    public int SuccessfulRequests { get; set; }
    public int FailedRequests { get; set; }
    public double AverageResponseTimeMs { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorDate { get; set; }

    // Quota and Validity
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    public int? DailyLimit { get; set; }
    public int? MonthlyLimit { get; set; }
    public int UsedQuota { get; set; }
    public int RemainingQuota { get; set; }

    // Navigation Property
    public virtual User User { get; set; } = null!;
}
