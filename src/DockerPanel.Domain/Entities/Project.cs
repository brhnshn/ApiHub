using System;
using System.Collections.Generic;
using DockerPanel.Domain.Enums;

namespace DockerPanel.Domain.Entities;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string? DockerContainerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ProjectType Type { get; set; } = ProjectType.DockerContainer;
    public string ImageOrPath { get; set; } = string.Empty;
    public long MemoryLimitBytes { get; set; }
    public double CpuCount { get; set; }
    public int InternalPort { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Provisioning;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }

    public bool EnablePhp { get; set; } = false;

    // Navigation Properties
    public virtual User User { get; set; } = null!;
    public virtual ICollection<Subdomain> Subdomains { get; set; } = new List<Subdomain>();
    public virtual ICollection<DnsRecord> DnsRecords { get; set; } = new List<DnsRecord>();
    public virtual ICollection<DatabaseSchema> DatabaseSchemas { get; set; } = new List<DatabaseSchema>();
}
