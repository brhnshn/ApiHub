using System;

namespace DockerPanel.Domain.Entities;

public class ExistingDatabaseInfo
{
    public string DbName { get; set; } = string.Empty;
    public string DbUser { get; set; } = string.Empty;
    public long SizeInBytes { get; set; }
}
