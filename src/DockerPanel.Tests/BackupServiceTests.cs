using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Infrastructure.Services;

namespace DockerPanel.Tests;

[Collection("Sequential")]
public class BackupServiceTests : IDisposable
{
    private readonly string _backupsDir;
    private readonly BackupService _backupService;

    public BackupServiceTests()
    {
        _backupsDir = Path.Combine(AppContext.BaseDirectory, "opt_dockerpanel", "backups");
        if (Directory.Exists(_backupsDir))
        {
            Directory.Delete(_backupsDir, true);
        }
        Directory.CreateDirectory(_backupsDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();

        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        _backupService = new BackupService(config, new FakeAuditLogService(), sp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_backupsDir))
        {
            try { Directory.Delete(_backupsDir, true); } catch { }
        }
    }

    [Fact]
    public async Task TriggerBackupAsync_RetainsOnlyLatestBackup_WhenTriggeredMultipleTimes()
    {
        // 1. Create a simulated older backup folder with manifest
        var oldBackupDir = Path.Combine(_backupsDir, "backup_2026-08-01_10-00-00");
        Directory.CreateDirectory(oldBackupDir);
        await File.WriteAllTextAsync(Path.Combine(oldBackupDir, "manifest.json"), "{}");

        var nonBackupDir = Path.Combine(_backupsDir, "important_custom_data");
        Directory.CreateDirectory(nonBackupDir);
        await File.WriteAllTextAsync(Path.Combine(nonBackupDir, "keep_me.txt"), "data");

        // 2. Trigger new backup
        await _backupService.TriggerBackupAsync(Guid.NewGuid());

        // 3. Verify that old backup is cleaned, non-backup dir is preserved, and exactly 1 backup folder remains
        var backupDirs = Directory.GetDirectories(_backupsDir, "backup_*");
        Assert.Single(backupDirs);
        Assert.False(Directory.Exists(oldBackupDir));
        Assert.True(Directory.Exists(nonBackupDir));

        // 4. Trigger another backup
        var firstNewBackupDir = backupDirs[0];
        await Task.Delay(1100); // Ensure distinct timestamp
        await _backupService.TriggerBackupAsync(Guid.NewGuid());

        var backupDirsSecondRun = Directory.GetDirectories(_backupsDir, "backup_*");
        Assert.Single(backupDirsSecondRun);
        Assert.False(Directory.Exists(firstNewBackupDir));
        Assert.True(Directory.Exists(nonBackupDir));
    }

    private class FakeAuditLogService : IAuditLogService
    {
        public Task LogAsync(Guid userId, string action, string targetEntity, Guid? targetId, string details, string ipAddress, string userAgent)
        {
            return Task.CompletedTask;
        }
    }
}
