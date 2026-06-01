using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace DockerPanel.Mobile.Services;

public class AutoUpdateService
{
    private readonly HttpClient _httpClient;
    public string CurrentVersion => AppInfo.VersionString;

    public AutoUpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        CleanOldApkFiles();
    }

    private void CleanOldApkFiles()
    {
        try
        {
            var cacheDir = FileSystem.CacheDirectory;
            if (Directory.Exists(cacheDir))
            {
                var apkFiles = Directory.GetFiles(cacheDir, "*.apk");
                foreach (var apkFile in apkFiles)
                {
                    File.Delete(apkFile);
                }
            }
        }
        catch
        {
            // Fail silently
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<VersionResponse>("api/downloads/version");
            if (response?.ApkAvailable == true &&
                Version.TryParse(response.Version, out var serverVer) &&
                Version.TryParse(CurrentVersion, out var localVer))
            {
                if (serverVer > localVer)
                {
                    return new UpdateCheckResult
                    {
                        HasUpdate = true,
                        ServerVersion = response.Version,
                        Changelog = response.Changelog ?? "Hata düzeltmeleri ve iyileştirmeler."
                    };
                }
            }
        }
        catch
        {
            // Fail silently
        }

        return new UpdateCheckResult { HasUpdate = false };
    }

    public async Task<bool> InstallUpdateAsync(string serverVersion)
    {
        try
        {
            // Download the APK
            var apkBytes = await _httpClient.GetByteArrayAsync("api/downloads/apk");
            var apkPath = Path.Combine(FileSystem.CacheDirectory, $"apihub_v{serverVersion}.apk");
            
            await File.WriteAllBytesAsync(apkPath, apkBytes);

            // Install APK natively on Android
#if ANDROID
            var context = Platform.CurrentActivity;
            if (context != null)
            {
                var file = new Java.IO.File(apkPath);
                var apkUri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, $"{context.PackageName}.fileprovider", file);
                
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionView);
                intent.SetDataAndType(apkUri, "application/vnd.android.package-archive");
                intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                
                context.StartActivity(intent);
                return true;
            }
#endif
        }
        catch
        {
            // Fail silently
        }

        return false;
    }

    private class VersionResponse
    {
        public string Version { get; set; } = "1.0.0";
        public bool ApkAvailable { get; set; }
        public string? Changelog { get; set; }
    }
}

public class UpdateCheckResult
{
    public bool HasUpdate { get; set; }
    public string ServerVersion { get; set; } = "1.0.0";
    public string Changelog { get; set; } = string.Empty;
}
