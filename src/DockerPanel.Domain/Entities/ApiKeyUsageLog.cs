using System;

namespace DockerPanel.Domain.Entities;

public class ApiKeyUsageLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApiKeyId { get; set; }
    public DateTimeOffset RequestDate { get; set; } = DateTimeOffset.UtcNow;
    public string? ProjectName { get; set; }
    public string? Endpoint { get; set; }
    public double ResponseTimeMs { get; set; }
    public int HttpStatus { get; set; }
    public int? TokenUsage { get; set; }
    public decimal? Cost { get; set; }

    // Navigation Property
    public virtual ApiKey ApiKey { get; set; } = null!;
}
