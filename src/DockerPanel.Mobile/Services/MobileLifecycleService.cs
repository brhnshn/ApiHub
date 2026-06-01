using System;

namespace DockerPanel.Mobile.Services;

public class MobileLifecycleService
{
    public event Action<bool>? OnAppStateChanged;
    public bool IsAppActive { get; private set; } = true;

    public void SetAppState(bool isAppActive)
    {
        if (IsAppActive == isAppActive) return;
        IsAppActive = isAppActive;
        OnAppStateChanged?.Invoke(IsAppActive);
    }
}
