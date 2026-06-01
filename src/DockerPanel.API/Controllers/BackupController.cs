using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Enums;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/backups")]
public class BackupController : ControllerBase
{
    private readonly IBackupService _backupService;

    public BackupController(IBackupService backupService)
    {
        _backupService = backupService;
    }

    private Guid GetUserId()
    {
        var idStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }

    private bool IsAdmin()
    {
        return User.IsInRole(UserRole.Administrator.ToString());
    }

    [HttpGet]
    public async Task<IActionResult> GetBackups()
    {
        if (!IsAdmin()) return Forbid();
        var backups = await _backupService.GetBackupsAsync();
        return Ok(backups);
    }

    [HttpPost("trigger")]
    public IActionResult TriggerBackup()
    {
        if (!IsAdmin()) return Forbid();

        if (_backupService.IsBackupActive)
        {
            return Conflict(new { Message = "Yedekleme işlemi zaten arka planda çalışıyor." });
        }

        // Asenkron / Fire-and-forget background execution, trigger immediately and return 202
        var userId = GetUserId();
        _ = Task.Run(async () =>
        {
            try
            {
                await _backupService.TriggerBackupAsync(userId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Manual Backup Task Error] {ex.Message}");
            }
        });

        return Accepted(new { Message = "Yedekleme işlemi arka planda başlatıldı." });
    }

    [HttpPost("{folderName}/restore/{type}")]
    public async Task<IActionResult> RestoreBackup(string folderName, string type)
    {
        if (!IsAdmin()) return Forbid();

        if (string.IsNullOrWhiteSpace(folderName) || !System.Text.RegularExpressions.Regex.IsMatch(folderName, "^backup_[0-9_-]+$"))
        {
            return BadRequest(new { Message = "Geçersiz yedek klasörü adı!" });
        }

        if (string.IsNullOrWhiteSpace(type) || !new[] { "database", "projects", "nginx", "mail" }.Contains(type.ToLowerInvariant()))
        {
            return BadRequest(new { Message = "Geçersiz yedek tipi!" });
        }

        var userId = GetUserId();

        try
        {
            await _backupService.RestoreBackupAsync(userId, folderName, type);
            return Ok(new { Message = "Geri yükleme işlemi başarıyla tamamlandı." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = $"Geri yükleme başarısız: {ex.Message}" });
        }
    }

    [HttpDelete("{folderName}")]
    public async Task<IActionResult> DeleteBackup(string folderName)
    {
        if (!IsAdmin()) return Forbid();

        if (string.IsNullOrWhiteSpace(folderName) || !System.Text.RegularExpressions.Regex.IsMatch(folderName, "^backup_[0-9_-]+$"))
        {
            return BadRequest(new { Message = "Geçersiz yedek klasörü adı!" });
        }

        var userId = GetUserId();

        try
        {
            await _backupService.DeleteBackupAsync(userId, folderName);
            return Ok(new { Message = "Yedek başarıyla silindi." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = $"Yedek silinemedi: {ex.Message}" });
        }
    }

    [HttpGet("{folderName}/download/{type}")]
    public async Task<IActionResult> DownloadBackup(string folderName, string type)
    {
        if (!IsAdmin()) return Forbid();

        if (string.IsNullOrWhiteSpace(folderName) || !System.Text.RegularExpressions.Regex.IsMatch(folderName, "^backup_[0-9_-]+$"))
        {
            return BadRequest(new { Message = "Geçersiz yedek klasörü adı!" });
        }

        if (string.IsNullOrWhiteSpace(type) || !new[] { "database", "projects", "nginx", "mail" }.Contains(type.ToLowerInvariant()))
        {
            return BadRequest(new { Message = "Geçersiz yedek tipi!" });
        }

        var userId = GetUserId();

        try
        {
            var fileStream = await _backupService.DownloadBackupFileAsync(userId, folderName, type);
            var extension = type.Equals("database", StringComparison.OrdinalIgnoreCase) ? "sql.gz" : "tar.gz";
            var outputName = $"{folderName}_{type}.{extension}";
            return File(fileStream, "application/octet-stream", outputName);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = $"Dosya indirme başarısız: {ex.Message}" });
        }
    }

    [HttpGet("remote-settings")]
    public async Task<IActionResult> GetRemoteBackupSettings()
    {
        if (!IsAdmin()) return Forbid();
        var settings = await _backupService.GetRemoteBackupSettingsAsync();
        return Ok(settings);
    }

    [HttpPost("remote-settings")]
    public async Task<IActionResult> SaveRemoteBackupSettings([FromBody] RemoteBackupSettingsDto settings)
    {
        if (!IsAdmin()) return Forbid();
        if (settings == null) return BadRequest("Settings cannot be null.");
        await _backupService.SaveRemoteBackupSettingsAsync(settings);
        return Ok(new { Message = "Uzak yedekleme SSH ayarları başarıyla kaydedildi." });
    }

    [HttpGet("ssh-public-key")]
    public async Task<IActionResult> GetSshPublicKey()
    {
        if (!IsAdmin()) return Forbid();
        var publicKey = await _backupService.GetSshPublicKeyAsync();
        return Ok(new { PublicKey = publicKey });
    }

    [HttpPost("test-ssh")]
    public async Task<IActionResult> TestSshConnection([FromBody] RemoteBackupSettingsDto settings)
    {
        if (!IsAdmin()) return Forbid();
        if (settings == null) return BadRequest("Settings cannot be null.");
        var result = await _backupService.TestSshConnectionAsync(settings);
        return Ok(new { Success = result.Success, Message = result.Message });
    }
}
