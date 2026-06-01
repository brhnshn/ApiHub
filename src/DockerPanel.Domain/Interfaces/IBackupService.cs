using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DockerPanel.Domain.Interfaces;

public interface IBackupService
{
    bool IsBackupActive { get; }
    Task<List<BackupInfoDto>> GetBackupsAsync();
    Task TriggerBackupAsync(Guid userId);
    Task RestoreBackupAsync(Guid userId, string folderName, string type);
    Task DeleteBackupAsync(Guid userId, string folderName);
    Task<Stream> DownloadBackupFileAsync(Guid userId, string folderName, string type);
    Task<RemoteBackupSettingsDto> GetRemoteBackupSettingsAsync();
    Task SaveRemoteBackupSettingsAsync(RemoteBackupSettingsDto settings);
    Task<string> GetSshPublicKeyAsync();
    Task<(bool Success, string Message)> TestSshConnectionAsync(RemoteBackupSettingsDto settings);
}

public class BackupInfoDto
{
    public string FolderName { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string DatabaseSize { get; set; } = "0 KB";
    public string ProjectsSize { get; set; } = "0 KB";
    public string NginxSize { get; set; } = "0 KB";
    public string MailSize { get; set; } = "0 KB";
    public string TotalSize { get; set; } = "0 KB";
    public string Status { get; set; } = "success";
    public string? ErrorMessage { get; set; }
}

public class RemoteBackupSettingsDto
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public string Port { get; set; } = "22";
    public string User { get; set; } = "root";
    public string AuthType { get; set; } = "key"; // "key" or "password"
    public string Password { get; set; } = string.Empty;
    public string KeyContent { get; set; } = string.Empty;
    public string KeyPath { get; set; } = "/opt/dockerpanel/remote_id_rsa";
    public string RemotePath { get; set; } = "/opt/dockerpanel/backups/";
}
