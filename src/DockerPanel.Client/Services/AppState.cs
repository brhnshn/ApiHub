using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using DockerPanel.Client.Security;
using Microsoft.JSInterop;

namespace DockerPanel.Client.Services
{
    public class AppState : IDisposable
    {
        private readonly HttpClient _http;
        private readonly NavigationManager _navigationManager;
        private readonly IJSRuntime _jsRuntime;
        private readonly IAuthTokenStore _tokenStore;
        private readonly PlatformInfo _platformInfo;
        private readonly object _lock = new();

        public event Action? OnStateChanged;

        // Cached Data
        public int RunningContainerCount { get; private set; }
        public double SystemCpu { get; private set; }
        public double RamUsedGb { get; private set; }
        public double RamTotalGb { get; private set; } = 8.0;
        public double DiskUsedPercentage { get; private set; }
        public double DiskUsedGb { get; private set; }
        public double DiskTotalGb { get; private set; }
        public int SubdomainCount { get; private set; }
        public int CpuCount { get; private set; } = 1;
        public string CpuModel { get; private set; } = "İşlemci...";

        public bool DockerActive { get; private set; }
        public string DockerVersion { get; private set; } = "Bilinmiyor";
        public string DockerApiVersion { get; private set; } = "Bilinmiyor";
        public bool NginxActive { get; private set; }
        public bool MailServerActive { get; private set; }
        public bool IsFcmConfigured { get; private set; }
        public int NotificationUnreadCount { get; private set; }

        private List<double> CpuHistory { get; } = new();
        private List<double> RamHistory { get; } = new();
        private List<string> SystemLogs { get; } = new();

        // Sayfa Bazlı Önbellek Verileri (Instant-Load)
        public List<ProjectCardStateDto>? CachedProjects { get; set; }

        // Canlı Metrik ve Log Akışı Event'leri (Instant-Resume)
        public event Action<ProjectMetricStateDto>? OnProjectMetricReceived;
        public event Action<ProjectLogsStateDto>? OnProjectLogsReceived;
        public event Func<Task>? OnPullToRefresh;

        public async Task TriggerPullToRefreshAsync()
        {
            if (OnPullToRefresh != null)
            {
                await OnPullToRefresh.Invoke();
            }
        }

        public HubConnection? HubConnection { get; private set; }
        public bool IsInitialized { get; private set; }
        public bool IsLoading { get; private set; }
        public bool IsSignalRConnected { get; private set; }
        public bool IsConnectionFailed { get; private set; }
        public string? ConnectionErrorMessage { get; private set; }
        public bool IsOfflineMode { get; private set; }
        public DateTime? LastOfflineSyncTime { get; private set; }

        public AppState(HttpClient http, NavigationManager navigationManager, IJSRuntime jsRuntime, IAuthTokenStore tokenStore, PlatformInfo platformInfo)
        {
            _http = http;
            _navigationManager = navigationManager;
            _jsRuntime = jsRuntime;
            _tokenStore = tokenStore;
            _platformInfo = platformInfo;

            // Subscribe to mobile background lifecycle events to optimize battery & network
            _platformInfo.OnAppStateChanged += HandleMobileAppStateChanged;
        }

        // Thread-safe copy accessors
        public List<double> GetCpuHistory()
        {
            lock (_lock)
            {
                return CpuHistory.ToList();
            }
        }

        public List<double> GetRamHistory()
        {
            lock (_lock)
            {
                return RamHistory.ToList();
            }
        }

        public List<string> GetSystemLogs()
        {
            lock (_lock)
            {
                return SystemLogs.ToList();
            }
        }

        public async Task ForceReloadAsync()
        {
            lock (_lock)
            {
                IsInitialized = false;
                IsLoading = false;
                IsConnectionFailed = false;
                ConnectionErrorMessage = null;
                if (HubConnection != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await HubConnection.DisposeAsync(); } catch { }
                    });
                    HubConnection = null;
                }
            }
            await InitializeAsync();
        }

        public async Task InitializeAsync()
        {
            string? token = null;
            try
            {
                token = await _tokenStore.GetTokenAsync();
            }
            catch {}

            if (IsInitialized || IsLoading)
            {
                // If we completed anonymous connection check before, but now we have a token,
                // we MUST re-initialize to fetch actual dashboard data!
                if (!string.IsNullOrWhiteSpace(token) && CachedProjects == null)
                {
                    lock (_lock)
                    {
                        IsInitialized = false;
                    }
                }
                else
                {
                    return;
                }
            }

            IsLoading = true;
            IsConnectionFailed = false;
            ConnectionErrorMessage = null;
            NotifyStateChanged();

            try
            {
                // Eşzamanlı Ön-Yükleme ve Zaman Aşımı Yarışı (Maksimum 700ms)
                var loadTask = LoadAllDataWithRetryAsync();
                var timeoutTask = Task.Delay(700);

                var completedTask = await Task.WhenAny(loadTask, timeoutTask);
                
                // Zaman aşımı veya veri yüklemesi bittiğinde yükleyici ekranını derhal kaldır
                await HideLoaderAsync();

                // Arka planda eğer yükleme bitmediyse tamamlanması sağlanır
                if (completedTask == timeoutTask)
                {
                    Console.WriteLine("[AppState] Pre-fetching hit 700ms timeout. Continuing loading in background...");
                    _ = CompleteInitializationInBackgroundAsync(loadTask);
                }
                else
                {
                    await loadTask;
                    IsInitialized = true;
                    IsLoading = false;
                    NotifyStateChanged();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppState] Initialization error: {ex.Message}");
                await HideLoaderAsync();
                IsLoading = false;
                
                if (ex is HttpRequestException hex && hex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    IsConnectionFailed = false;
                    ConnectionErrorMessage = null;
                }
                else
                {
                    var loadedFromCache = await TryLoadFromOfflineCacheAsync();
                    if (loadedFromCache)
                    {
                        IsOfflineMode = true;
                        IsInitialized = true;
                        IsConnectionFailed = false;
                    }
                    else
                    {
                        IsConnectionFailed = true;
                        ConnectionErrorMessage = "Sunucuya bağlanılamadı. Lütfen sunucu adresinizi veya internet bağlantınızı kontrol edin.";
                    }
                }
                NotifyStateChanged();
            }
        }

        private async Task HideLoaderAsync()
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("window.hideLoader");
            }
            catch
            {
                // Fallback in case JS is not ready yet
            }
        }

        private async Task LoadAllDataWithRetryAsync()
        {
            int maxRetries = 3;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    await LoadAllDataAsync();
                    return;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AppState] LoadAllDataAsync attempt {i + 1} failed: {ex.Message}");
                    if (i == maxRetries - 1)
                    {
                        throw;
                    }
                    await Task.Delay(1000);
                }
            }
        }

        private async Task LoadAllDataAsync()
        {
            using var cts = new System.Threading.CancellationTokenSource(3500);

            // Check if the user is authenticated (has a token stored)
            string? token = null;
            try
            {
                token = await _tokenStore.GetTokenAsync(cts.Token);
            }
            catch
            {
                // Fallback for startup JS interop / storage limits
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                // Anonymous mode: Verify if the server is connected by hitting the public setup-status endpoint.
                try
                {
                    var response = await _http.GetAsync("api/auth/setup-status", cts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"Server returned unsuccessful status code: {response.StatusCode}", null, response.StatusCode);
                    }
                    
                    lock (_lock)
                    {
                        IsConnectionFailed = false;
                        ConnectionErrorMessage = null;
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    // Anonymous endpoint returned 401 (unexpected, but server is online and responding)
                    lock (_lock)
                    {
                        IsConnectionFailed = false;
                        ConnectionErrorMessage = null;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AppState] Anonymous server connection check failed: {ex.Message}");
                    lock (_lock)
                    {
                        IsConnectionFailed = true;
                        ConnectionErrorMessage = "Sunucuya bağlanılamadı. Lütfen sunucu adresinizi veya internet bağlantınızı kontrol edin.";
                    }
                    throw;
                }
                return;
            }

            // Authenticated mode: Load all dashboard data
            try
            {
                var containersTask = _http.GetFromJsonAsync<List<ContainerStateDto>>("api/projects", cts.Token);
                var subdomainsTask = _http.GetFromJsonAsync<List<SubdomainStateDto>>("api/nginx", cts.Token);
                var systemStatusTask = _http.GetFromJsonAsync<SystemStatusStateDto>("api/system/status", cts.Token);

                await Task.WhenAll(containersTask, subdomainsTask, systemStatusTask);

                var containers = await containersTask;
                lock (_lock)
                {
                    RunningContainerCount = containers?.Count(c => c.Status == 0) ?? 0; // 0: Running
                }

                var subdomains = await subdomainsTask;
                lock (_lock)
                {
                    SubdomainCount = subdomains?.Count ?? 0;
                }

                var systemStatus = await systemStatusTask;
                if (systemStatus != null)
                {
                    lock (_lock)
                    {
                        DockerActive = systemStatus.DockerActive;
                        DockerVersion = systemStatus.DockerVersion;
                        DockerApiVersion = systemStatus.DockerApiVersion;
                        NginxActive = systemStatus.NginxActive;
                        MailServerActive = systemStatus.MailServerActive;
                        CpuCount = systemStatus.CpuCount;
                        CpuModel = systemStatus.CpuModel;
                        IsFcmConfigured = systemStatus.IsFcmConfigured;
                    }
                }

                // Save to local storage for offline view
                try
                {
                    var cacheData = new CachedDashboardData
                    {
                        Containers = containers,
                        Subdomains = subdomains,
                        SystemStatus = systemStatus,
                        LastSyncTime = DateTime.Now
                    };
                    var json = System.Text.Json.JsonSerializer.Serialize(cacheData);
                    await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "cached_dashboard_data", json);
                }
                catch {}

                lock (_lock)
                {
                    IsOfflineMode = false;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // This is a 401 authentication error, NOT a connection failure.
                // Let the JwtAuthorizationHandler handle the logout redirect.
                lock (_lock)
                {
                    IsConnectionFailed = false;
                    ConnectionErrorMessage = null;
                }
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppState] LoadAllDataAsync API error: {ex.Message}");
                lock (_lock)
                {
                    IsConnectionFailed = true;
                    ConnectionErrorMessage = "Sunucu bağlantı hatası verdi veya zaman aşımına uğradı.";
                }
                throw;
            }

            // SignalR bağlantısını arka planda başlat (el sıkışması boot loader'ı engellemesin)
            _ = SetupSignalRAsync();
        }

        private async Task CompleteInitializationInBackgroundAsync(Task loadTask)
        {
            try
            {
                await loadTask;
                IsInitialized = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppState] Background loading error: {ex.Message}");
                if (ex is HttpRequestException hex && hex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    IsConnectionFailed = false;
                    ConnectionErrorMessage = null;
                }
                else
                {
                    var loadedFromCache = await TryLoadFromOfflineCacheAsync();
                    if (loadedFromCache)
                    {
                        IsOfflineMode = true;
                        IsInitialized = true;
                        IsConnectionFailed = false;
                    }
                    else
                    {
                        IsConnectionFailed = true;
                        ConnectionErrorMessage = "Sunucuya bağlanılamadı. Lütfen sunucu adresinizi veya internet bağlantınızı kontrol edin.";
                    }
                }
            }
            finally
            {
                IsLoading = false;
                NotifyStateChanged();
            }
        }

        private class CachedDashboardData
        {
            public List<ContainerStateDto>? Containers { get; set; }
            public List<SubdomainStateDto>? Subdomains { get; set; }
            public SystemStatusStateDto? SystemStatus { get; set; }
            public DateTime LastSyncTime { get; set; }
        }

        private async Task<bool> TryLoadFromOfflineCacheAsync()
        {
            try
            {
                var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", "cached_dashboard_data");
                if (string.IsNullOrWhiteSpace(json)) return false;

                var data = System.Text.Json.JsonSerializer.Deserialize<CachedDashboardData>(json);
                if (data == null) return false;

                lock (_lock)
                {
                    RunningContainerCount = data.Containers?.Count(c => c.Status == 0) ?? 0;
                    SubdomainCount = data.Subdomains?.Count ?? 0;
                    if (data.SystemStatus != null)
                    {
                        DockerActive = data.SystemStatus.DockerActive;
                        DockerVersion = data.SystemStatus.DockerVersion;
                        DockerApiVersion = data.SystemStatus.DockerApiVersion;
                        NginxActive = data.SystemStatus.NginxActive;
                        MailServerActive = data.SystemStatus.MailServerActive;
                        CpuCount = data.SystemStatus.CpuCount;
                        CpuModel = data.SystemStatus.CpuModel;
                        IsFcmConfigured = data.SystemStatus.IsFcmConfigured;
                    }
                    LastOfflineSyncTime = data.LastSyncTime;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppState] Failed to load offline cache: {ex.Message}");
                return false;
            }
        }

        private async Task SetupSignalRAsync()
        {
            if (HubConnection != null) return;

            try
            {
                // API Base Address'ten SignalR Hub Url'i oluşturuluyor (WebSocket port uyuşmazlığı giderildi)
                var baseUri = _http.BaseAddress?.ToString() ?? _navigationManager.BaseUri;
                var hubUrl = baseUri.TrimEnd('/') + "/hubs/metriclog";

                var connection = new HubConnectionBuilder()
                    .WithUrl(hubUrl, options =>
                    {
                        options.Transports =
                            Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets |
                            Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                        options.AccessTokenProvider = async () =>
                        {
                            return await _tokenStore.GetTokenAsync();
                        };
                    })
                    .WithAutomaticReconnect()
                    .Build();

                connection.Reconnecting += error =>
                {
                    IsSignalRConnected = false;
                    lock (_lock)
                    {
                        SystemLogs.Add("[UYARI] Real-time SignalR baglantisi koptu, yeniden baglaniliyor.");
                    }
                    NotifyStateChanged();
                    return Task.CompletedTask;
                };

                connection.Reconnected += connectionId =>
                {
                    IsSignalRConnected = true;
                    lock (_lock)
                    {
                        SystemLogs.Add("[INFO] Real-time SignalR baglantisi yeniden kuruldu.");
                    }
                    NotifyStateChanged();
                    return Task.CompletedTask;
                };

                connection.Closed += error =>
                {
                    IsSignalRConnected = false;
                    lock (_lock)
                    {
                        SystemLogs.Add("[UYARI] Real-time SignalR baglantisi kapandi.");
                    }
                    NotifyStateChanged();
                    return Task.CompletedTask;
                };

                connection.On<SystemMetricsStateDto>("ReceiveSystemMetrics", (metrics) =>
                {
                    lock (_lock)
                    {
                        SystemCpu = metrics.Cpu;
                        RamUsedGb = metrics.RamUsedGb;
                        RamTotalGb = metrics.RamTotalGb;
                        DiskUsedPercentage = metrics.DiskUsedPercentage;
                        DiskUsedGb = metrics.DiskUsedGb;
                        DiskTotalGb = metrics.DiskTotalGb;

                        CpuHistory.Add(metrics.Cpu);
                        if (CpuHistory.Count > 20) CpuHistory.RemoveAt(0);

                        RamHistory.Add(metrics.RamPercentage);
                        if (RamHistory.Count > 20) RamHistory.RemoveAt(0);
                    }
                    NotifyStateChanged();
                });

                connection.On<List<SystemLogLineStateDto>>("ReceiveSystemLogs", (logs) =>
                {
                    lock (_lock)
                    {
                        foreach (var logLine in logs)
                        {
                            SystemLogs.Add($"[{logLine.Level.ToUpper()}]: {logLine.Message}");
                            if (SystemLogs.Count > 100) SystemLogs.RemoveAt(0);
                        }
                    }
                    NotifyStateChanged();
                });

                // Global Proje Metrikleri Akış Dinleyicisi
                connection.On<ProjectMetricStateDto>("ReceiveProjectMetrics", (metric) =>
                {
                    if (CachedProjects != null)
                    {
                        var project = CachedProjects.FirstOrDefault(p => p.Id == metric.ProjectId);
                        if (project != null)
                        {
                            project.LiveCpu = metric.Cpu;
                            project.LiveRamBytes = metric.RamBytes;
                            project.LiveRamPct = project.MemoryLimitBytes > 0 
                                ? Math.Round(((double)metric.RamBytes / project.MemoryLimitBytes) * 100.0, 1) 
                                : 0;
                        }
                    }
                    OnProjectMetricReceived?.Invoke(metric);
                });

                // Global Proje Log Akış Dinleyicisi
                connection.On<ProjectLogsStateDto>("ReceiveProjectLogs", (logDto) =>
                {
                    OnProjectLogsReceived?.Invoke(logDto);
                });

                await connection.StartAsync();
                
                lock (_lock)
                {
                    HubConnection = connection;
                    IsSignalRConnected = true;
                    SystemLogs.Add("[INFO] Real-time SignalR kontrol kanalı bağlantısı kuruldu.");
                }
                NotifyStateChanged();
            }
            catch (Exception ex)
            {
                IsSignalRConnected = false;
                lock (_lock)
                {
                    SystemLogs.Add($"[HATA] Real-time SignalR bağlantısı kurulamadı: {ex.Message}");
                }
            }
        }

        public async Task JoinProjectGroupAsync(string projectId)
        {
            if (HubConnection != null && HubConnection.State == HubConnectionState.Connected)
            {
                try
                {
                    await HubConnection.SendAsync("JoinProjectGroup", projectId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AppState] JoinProjectGroup failed: {ex.Message}");
                }
            }
        }

        public async Task LeaveProjectGroupAsync(string projectId)
        {
            if (HubConnection != null && HubConnection.State == HubConnectionState.Connected)
            {
                try
                {
                    await HubConnection.SendAsync("LeaveProjectGroup", projectId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AppState] LeaveProjectGroup failed: {ex.Message}");
                }
            }
        }

        public void AddSystemLog(string message)
        {
            lock (_lock)
            {
                SystemLogs.Add(message);
                if (SystemLogs.Count > 300) SystemLogs.RemoveAt(0);
            }
            NotifyStateChanged();
        }

        public void ClearTerminal()
        {
            lock (_lock)
            {
                SystemLogs.Clear();
            }
            NotifyStateChanged();
        }

        public void Reset()
        {
            lock (_lock)
            {
                RunningContainerCount = 0;
                SystemCpu = 0;
                RamUsedGb = 0;
                DiskUsedPercentage = 0;
                DiskUsedGb = 0;
                DiskTotalGb = 0;
                SubdomainCount = 0;
                DockerActive = false;
                DockerVersion = "Bilinmiyor";
                DockerApiVersion = "Bilinmiyor";
                NginxActive = false;
                MailServerActive = false;
                NotificationUnreadCount = 0;

                CpuHistory.Clear();
                RamHistory.Clear();
                SystemLogs.Clear();

                IsInitialized = false;
                IsLoading = false;
                IsSignalRConnected = false;

                if (HubConnection != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HubConnection.DisposeAsync();
                        }
                        catch { }
                    });
                    HubConnection = null;
                }
            }
            NotifyStateChanged();
        }

        public void UpdateNotificationUnreadCount(int count)
        {
            lock (_lock)
            {
                NotificationUnreadCount = count;
            }
            NotifyStateChanged();
        }

        private void NotifyStateChanged() => OnStateChanged?.Invoke();

        private void HandleMobileAppStateChanged(bool isActive)
        {
            Console.WriteLine($"[AppState] Mobile lifecycle state changed: isActive={isActive}");
            if (!isActive)
            {
                // Arka plana geçildiğinde SignalR bağlantısını kes ve kaynakları serbest bırak (Pil/Veri tasarrufu)
                if (HubConnection != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HubConnection.StopAsync();
                            IsSignalRConnected = false;
                            NotifyStateChanged();
                        }
                        catch {}
                    });
                }
                
                // RAM'i optimize etmek için Garbage Collector'ı zorla tetikle
                try
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                catch {}
            }
            else
            {
                // Ön plana gelindiğinde SignalR bağlantısını yeniden kur (Anlık güncelleme)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200); // UI'ın yerleşmesi için kısa gecikme
                    await SetupSignalRAsync();
                });
            }
        }

        public void Dispose()
        {
            try
            {
                _platformInfo.OnAppStateChanged -= HandleMobileAppStateChanged;
            }
            catch {}
        }

        // Inner DTOs matching backend definitions
        private class ContainerStateDto
        {
            public Guid Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public int Status { get; set; } // 0: Running, etc.
        }

        private class SubdomainStateDto
        {
            public Guid Id { get; set; }
        }

        private class SystemStatusStateDto
        {
            public bool DockerActive { get; set; }
            public string DockerVersion { get; set; } = "Bilinmiyor";
            public string DockerApiVersion { get; set; } = "Bilinmiyor";
            public bool NginxActive { get; set; }
            public bool MailServerActive { get; set; }
            public int CpuCount { get; set; }
            public string CpuModel { get; set; } = "Bilinmiyor";
            public bool IsFcmConfigured { get; set; }
        }

        private class SystemMetricsStateDto
        {
            public double Cpu { get; set; }
            public double RamPercentage { get; set; }
            public double RamUsedGb { get; set; }
            public double RamTotalGb { get; set; }
            public double DiskUsedPercentage { get; set; }
            public double DiskUsedGb { get; set; }
            public double DiskTotalGb { get; set; }
        }

        private class SystemLogLineStateDto
        {
            public string Level { get; set; } = "info";
            public string Message { get; set; } = string.Empty;
        }
    }

    // Global sayfa durumu ve instant-load DTO'ları
    public class ProjectCardStateDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public StateProjectType Type { get; set; }
        public string ImageOrPath { get; set; } = string.Empty;
        public long MemoryLimitBytes { get; set; }
        public double CpuCount { get; set; }
        public int InternalPort { get; set; }
        public StateProjectStatus Status { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public bool EnablePhp { get; set; }
        
        // Canlı Değerler
        public double LiveCpu { get; set; }
        public long LiveRamBytes { get; set; }
        public double LiveRamPct { get; set; }
    }

    public enum StateProjectType
    {
        DockerContainer,
        NativeProject,
        StaticSite
    }

    public enum StateProjectStatus
    {
        Running,
        Stopped,
        Provisioning,
        Error
    }

    public class ProjectMetricStateDto
    {
        public Guid ProjectId { get; set; }
        public double Cpu { get; set; }
        public long RamBytes { get; set; }
        public double RamPercentage { get; set; }
    }

    public class ProjectLogsStateDto
    {
        public Guid ProjectId { get; set; }
        public List<string> Logs { get; set; } = new();
    }
}
