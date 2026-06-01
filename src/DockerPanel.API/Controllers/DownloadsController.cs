using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DockerPanel.API.Controllers;

[ApiController]
[Route("api/downloads")]
public class DownloadsController : ControllerBase
{
    private const string FallbackAppVersion = "1.0.0";
    private static readonly ConcurrentDictionary<string, (DateTime ExpiresAt, Guid UserId)> QrTokens = new();

    private class ApkMetadata
    {
        public string? Version { get; set; }
        public string? Changelog { get; set; }
    }

    private static string GetDownloadsDirectory()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Directory.GetCurrentDirectory(), "downloads")
            : "/opt/dockerpanel/downloads";
    }

    private static string GetApkPath()
    {
        return Path.Combine(GetDownloadsDirectory(), "apihub.apk");
    }

    private static FileInfo GetApkFileInfo()
    {
        return new FileInfo(GetApkPath());
    }

    private static string GetMetadataPath()
    {
        return Path.Combine(GetDownloadsDirectory(), "apihub.json");
    }

    private (string Version, string Changelog, bool Exists, long Length, DateTime LastModified) GetActiveApkDetails()
    {
        var fileInfo = GetApkFileInfo();
        var exists = fileInfo.Exists && fileInfo.Length > 0;
        if (!exists)
        {
            return (FallbackAppVersion, "Mobil APK paketi henuz sunucuya yuklenmedi.", false, 0, DateTime.MinValue);
        }

        string version = "";
        string changelog = "";

        // 1. Try reading custom metadata JSON
        var metaPath = GetMetadataPath();
        if (System.IO.File.Exists(metaPath))
        {
            try
            {
                var json = System.IO.File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<ApkMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (meta != null)
                {
                    if (!string.IsNullOrWhiteSpace(meta.Version))
                    {
                        version = meta.Version.Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(meta.Changelog))
                    {
                        changelog = meta.Changelog;
                    }
                }
            }
            catch
            {
                // Fallback to auto generation if JSON is corrupt or unreadable
            }
        }

        // 2. Generate fallback version if not set via JSON (format: 1.0.YYMMDD.HHMM)
        if (string.IsNullOrEmpty(version))
        {
            var timeUtc = fileInfo.LastWriteTimeUtc;
            version = $"1.0.{timeUtc:yyMMdd}.{timeUtc:HHmm}";
        }

        // 3. Generate fallback changelog if not set via JSON
        if (string.IsNullOrEmpty(changelog))
        {
            var sizeInMb = (double)fileInfo.Length / (1024 * 1024);
            var localTime = fileInfo.LastWriteTime;
            changelog = $"- Sunucu uzerindeki APK dosyasi guncellendi (Otomatik Surum: {version}).\n" +
                        $"- Yuklenme Tarihi: {localTime:dd.MM.yyyy HH:mm} (Yerel Saat)\n" +
                        $"- Dosya Boyutu: {sizeInMb:F2} MB";
        }

        return (version, changelog, true, fileInfo.Length, fileInfo.LastWriteTime);
    }

    private Guid GetUserId()
    {
        var idStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }

    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        var apk = GetActiveApkDetails();
        return Ok(new
        {
            Version = apk.Version,
            ApkAvailable = apk.Exists,
            Changelog = apk.Changelog
        });
    }

    [HttpGet("apk-metadata")]
    public IActionResult GetApkMetadata()
    {
        var apk = GetActiveApkDetails();
        var sizeInMb = apk.Exists ? (double)apk.Length / (1024 * 1024) : 0;

        return Ok(new
        {
            Version = apk.Exists ? $"v{apk.Version}" : "APK hazir degil",
            SizeMb = sizeInMb,
            FormattedSize = apk.Exists ? $"{sizeInMb:F1} MB" : "Sunucuda APK yok",
            Label = "ApiHub Mobil Asistan",
            Available = apk.Exists,
            LastModified = apk.Exists ? apk.LastModified.ToString("dd.MM.yyyy HH:mm") : null
        });
    }

    [Authorize]
    [HttpGet("qr-token")]
    public IActionResult GenerateQrToken()
    {
        var userId = GetUserId();
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTime.UtcNow.AddMinutes(15);
        QrTokens.TryAdd(token, (expiresAt, userId));

        return Ok(new
        {
            Token = token,
            ExpiresAt = expiresAt
        });
    }

    [AllowAnonymous]
    [HttpGet("apk")]
    public IActionResult DownloadApk()
    {
        return DownloadApkFile();
    }

    [HttpGet("apk/{token}")]
    public IActionResult DownloadApkWithToken(string token)
    {
        if (!QrTokens.TryRemove(token, out var tokenInfo))
        {
            return BadRequest(new { Message = "Gecersiz veya suresi dolmus QR indirme tokeni." });
        }

        if (DateTime.UtcNow > tokenInfo.ExpiresAt)
        {
            return BadRequest(new { Message = "Bu QR kodun suresi dolmus. Lutfen panelden yeni bir QR kod tarayin." });
        }

        return DownloadApkFile();
    }

    private IActionResult DownloadApkFile()
    {
        var fileInfo = GetApkFileInfo();
        if (!fileInfo.Exists || fileInfo.Length <= 0)
        {
            return NotFound(new { Message = "APK dosyasi sunucuda bulunamadi." });
        }

        return PhysicalFile(fileInfo.FullName, "application/vnd.android.package-archive", "apihub.apk");
    }
}
