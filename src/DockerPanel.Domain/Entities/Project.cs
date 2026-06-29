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

    // Revision Fields
    public string? Description { get; set; }
    public string? Logo { get; set; }
    public string? RuntimeType { get; set; }
    public string? FrameworkVersion { get; set; }
    public bool AutoRestart { get; set; } = true;
    public string? HealthCheckEndpoint { get; set; }
    public string? RunUser { get; set; } = "root";
    public string? WorkingDirectory { get; set; }
    public string? Environment { get; set; } = "Production";
    public string? EntryFile { get; set; }
    public string? StartCommand { get; set; }
    public string EnvVariablesJson { get; set; } = "{}"; // JSON dictionary

    // PostgreSQL connection info if created for project
    public string? DatabaseName { get; set; }
    public string? DatabaseUser { get; set; }
    public string? DatabasePassword { get; set; }

    // Navigation Properties
    public virtual User User { get; set; } = null!;
    public virtual ICollection<Subdomain> Subdomains { get; set; } = new List<Subdomain>();
    public virtual ICollection<DnsRecord> DnsRecords { get; set; } = new List<DnsRecord>();
    public virtual ICollection<DatabaseSchema> DatabaseSchemas { get; set; } = new List<DatabaseSchema>();
}
