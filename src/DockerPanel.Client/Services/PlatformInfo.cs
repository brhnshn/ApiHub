using System;
using System.Threading.Tasks;

namespace DockerPanel.Client.Services;

public class PlatformInfo
{
    public bool IsMobileApp { get; set; }
    public string LocalVersion { get; set; } = "1.0.0";
    public Func<Task<string>>? GetServerUrlFunc { get; set; }
    public Func<string, Task>? SaveServerUrlFunc { get; set; }
    public Func<Task<string?>>? GetFcmTokenFunc { get; set; }
    public Func<string>? GetDeviceNameFunc { get; set; }
    public Func<string, Task<bool>>? TriggerApkInstallFunc { get; set; }
    public Func<Task<(bool HasUpdate, string ServerVersion, string Changelog)>>? CheckForUpdatesFunc { get; set; }
    public Func<Task<long>>? GetCacheSizeFunc { get; set; }
    public Func<Task>? ClearCacheFunc { get; set; }

    // Bridge for mobile application background lifecycle events
    public event Action<bool>? OnAppStateChanged;
    public void TriggerAppStateChanged(bool isActive) => OnAppStateChanged?.Invoke(isActive);
}
