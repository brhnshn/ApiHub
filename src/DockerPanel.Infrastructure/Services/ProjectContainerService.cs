using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Security;

namespace DockerPanel.Infrastructure.Services;

public class ProjectContainerService : IProjectContainerService
{
    private readonly DockerClient _dockerClient;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (ulong CpuTotal, ulong SystemTotal, DateTime Timestamp)> _prevCpuStats = new();

    public ProjectContainerService()
    {
        Uri dockerUri;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            dockerUri = new Uri("npipe://./pipe/docker_engine");
        }
        else
        {
            dockerUri = new Uri("unix:///var/run/docker.sock");
        }

        _dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
    }

    public async Task<string> ProvisionContainerAsync(string name, string imageName, long memoryLimitBytes, double cpuCount, int hostPort, int containerPort)
    {
        // 1. Regex Girdi Doğrulama (Command Injection Önleme)
        InputValidator.ThrowIfInvalidProjectName(name, "Uygulama adı sadece küçük harf, rakam, tire (-) ve alt çizgi (_) içerebilir!");

        SystemLogQueue.Log("info", $"[Docker] Konteyner sağlama işlemi başlatıldı: Uygulama={name}, Imaj={imageName}");

        // 2. Docker Imaj Kontrolü ve Pull Akışı
        var parts = imageName.Split(':');
        var fromImage = parts[0];
        var tag = parts.Length > 1 ? parts[1] : "latest";

        SystemLogQueue.Log("info", $"$ docker images -a | grep '{fromImage}:{tag}'");
        var images = await _dockerClient.Images.ListImagesAsync(new ImagesListParameters { All = true });
        bool imageExists = images.Any(img => img.RepoTags != null && img.RepoTags.Contains($"{fromImage}:{tag}"));

        if (!imageExists)
        {
            SystemLogQueue.Log("info", $"[Docker] İmaj yerelde bulunamadı, Docker Hub'dan çekiliyor...");
            SystemLogQueue.Log("info", $"$ docker pull {fromImage}:{tag}");
            await _dockerClient.Images.CreateImageAsync(
                new ImagesCreateParameters
                {
                    FromImage = fromImage,
                    Tag = tag
                },
                null,
                new Progress<JSONMessage>()
            );
            SystemLogQueue.Log("info", $"[Docker] İmaj başarıyla sunucuya çekildi: {fromImage}:{tag}");
        }
        else
        {
            SystemLogQueue.Log("info", $"[Docker] İmaj zaten yerelde mevcut: {fromImage}:{tag}");
        }

        // 3. Donanım Limit Hesaplamaları
        long nanoCpus = (long)(cpuCount * 1_000_000_000);

        // 4. Konteyner Yapılandırması ve Yaratımı
        // containerPortKey → image içinde gerçekten dinlenen port (örn. 80/tcp)
        // HostPort           → dış dünyaya/Nginx'e açılan port   (örn. 8080)
        var containerPortKey = $"{containerPort}/tcp";
        var config = new CreateContainerParameters
        {
            Image = $"{fromImage}:{tag}",
            Name = name,
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                { containerPortKey, new EmptyStruct() }
            },
            HostConfig = new HostConfig
            {
                NetworkMode = "dockerpanel-global-net",
                Memory = memoryLimitBytes,
                NanoCPUs = nanoCpus,
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [containerPortKey] = new List<PortBinding>
                    {
                        new()
                        {
                            HostIP = "127.0.0.1",
                            HostPort = hostPort.ToString()
                        }
                    }
                },
                RestartPolicy = new RestartPolicy
                {
                    Name = RestartPolicyKind.Always
                }
            }
        };

        // Network check / create bridge network if it doesn't exist
        var networks = await _dockerClient.Networks.ListNetworksAsync();
        if (!networks.Any(n => n.Name == "dockerpanel-global-net"))
        {
            SystemLogQueue.Log("info", $"$ docker network create --driver bridge dockerpanel-global-net");
            await _dockerClient.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = "dockerpanel-global-net",
                Driver = "bridge"
            });
            SystemLogQueue.Log("info", $"[Docker] global köprü ağı 'dockerpanel-global-net' oluşturuldu.");
        }

        SystemLogQueue.Log("info", $"$ docker create --name {name} --net dockerpanel-global-net -m {memoryLimitBytes} --cpus {cpuCount} -p 127.0.0.1:{hostPort}:{containerPort}/tcp {fromImage}:{tag}");
        var response = await _dockerClient.Containers.CreateContainerAsync(config);
        SystemLogQueue.Log("info", $"[Docker] Konteyner oluşturuldu. ID: {response.ID.Substring(0, 12)}");

        // 5. Başlatma Lojiği
        SystemLogQueue.Log("info", $"$ docker start {name}");
        await _dockerClient.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
        SystemLogQueue.Log("info", $"[Docker] Konteyner başarıyla sağlandı ve arka planda çalıştırıldı (Running).");

        return response.ID;
    }

    public async Task<int?> GetImageExposedPortAsync(string imageName)
    {
        // 1. Image adını ayrıştır
        var parts = imageName.Split(':');
        var fromImage = parts[0];
        var tag = parts.Length > 1 ? parts[1] : "latest";
        var fullName = $"{fromImage}:{tag}";

        // 2. Image yerelde mevcut mu kontrol et; yoksa pull et
        var images = await _dockerClient.Images.ListImagesAsync(new ImagesListParameters { All = true });
        bool imageExists = images.Any(img => img.RepoTags != null && img.RepoTags.Contains(fullName));

        if (!imageExists)
        {
            SystemLogQueue.Log("info", $"[Docker] ExposedPort tespiti için image çekiliyor: {fullName}");
            try
            {
                await _dockerClient.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = fromImage, Tag = tag },
                    null,
                    new Progress<JSONMessage>()
                );
                SystemLogQueue.Log("info", $"[Docker] Image başarıyla çekildi: {fullName}");
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("warning", $"[Docker] ExposedPort tespiti için image çekilemedi: {ex.Message}");
                return null;
            }
        }

        // 3. Inspect et ve ExposedPorts'dan ilk portu oku
        try
        {
            var inspect = await _dockerClient.Images.InspectImageAsync(fullName);
            if (inspect.Config?.ExposedPorts != null && inspect.Config.ExposedPorts.Count > 0)
            {
                // ExposedPorts keys: "80/tcp", "443/tcp", "3000/tcp" vb.
                foreach (var portKey in inspect.Config.ExposedPorts.Keys)
                {
                    var rawPort = portKey.Split('/')[0];
                    if (int.TryParse(rawPort, out int port))
                    {
                        SystemLogQueue.Log("info", $"[Docker] Image EXPOSE tespiti: {fullName} → {port}");
                        return port;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("warning", $"[Docker] Image inspect sırasında hata: {ex.Message}");
        }

        return null;
    }

    public async Task StopContainerAsync(string dockerContainerId)
    {
        SystemLogQueue.Log("warning", $"[Docker] Konteyner durduruluyor: ID={dockerContainerId.Substring(0, Math.Min(12, dockerContainerId.Length))}");
        SystemLogQueue.Log("info", $"$ docker stop -t 15 {dockerContainerId.Substring(0, Math.Min(12, dockerContainerId.Length))}");
        await _dockerClient.Containers.StopContainerAsync(dockerContainerId, new ContainerStopParameters
        {
            WaitBeforeKillSeconds = 15
        });
        SystemLogQueue.Log("info", $"[Docker] Konteyner başarıyla durduruldu.");
    }

    public async Task StartContainerAsync(string dockerContainerId)
    {
        SystemLogQueue.Log("info", $"[Docker] Konteyner başlatılıyor: ID={dockerContainerId.Substring(0, Math.Min(12, dockerContainerId.Length))}");
        SystemLogQueue.Log("info", $"$ docker start {dockerContainerId.Substring(0, Math.Min(12, dockerContainerId.Length))}");
        await _dockerClient.Containers.StartContainerAsync(dockerContainerId, new ContainerStartParameters());
        SystemLogQueue.Log("info", $"[Docker] Konteyner başarıyla başlatıldı.");
    }

    public async Task DeleteContainerAsync(string dockerContainerId)
    {
        // Önce durdur, sonra sil
        try
        {
            await StopContainerAsync(dockerContainerId);
        }
        catch
        {
            // Zaten durmuş olabilir
        }

        SystemLogQueue.Log("warning", $"[Docker] Konteyner sistemden tamamen yok ediliyor (Delete)...");
        SystemLogQueue.Log("info", $"$ docker rm -f {dockerContainerId.Substring(0, Math.Min(12, dockerContainerId.Length))}");
        await _dockerClient.Containers.RemoveContainerAsync(dockerContainerId, new ContainerRemoveParameters
        {
            Force = true
        });
        SystemLogQueue.Log("info", $"[Docker] Konteyner ve ilgili ağ arayüzleri başarıyla temizlendi.");
    }

    public async Task UpdateContainerLimitsAsync(string dockerContainerId, long memoryLimitBytes, double cpuCount)
    {
        if (string.IsNullOrWhiteSpace(dockerContainerId))
        {
            throw new ArgumentException("Konteyner ID boş olamaz.");
        }

        long nanoCpus = (long)(cpuCount * 1_000_000_000);
        SystemLogQueue.Log("info", $"$ docker update --memory {memoryLimitBytes} --cpus {cpuCount} {dockerContainerId.Substring(0, Math.Min(12, dockerContainerId.Length))}");

        await _dockerClient.Containers.UpdateContainerAsync(dockerContainerId, new ContainerUpdateParameters
        {
            Memory = memoryLimitBytes,
            NanoCPUs = nanoCpus
        });

        SystemLogQueue.Log("info", $"[Docker] Konteyner kaynak limitleri güncellendi.");
    }

    public async Task<ContainerStatsDto> GetContainerStatsAsync(string dockerContainerId)
    {
        var statsDto = new ContainerStatsDto();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(3000);

        try
        {
            await _dockerClient.Containers.GetContainerStatsAsync(dockerContainerId,
                new ContainerStatsParameters { Stream = false },
                new Progress<ContainerStatsResponse>(stats =>
                {
                    if (stats?.CPUStats == null) return;

                    ulong currentCpu = stats.CPUStats.CPUUsage?.TotalUsage ?? 0;
                    ulong currentSystem = stats.CPUStats.SystemUsage;

                    double cpuDelta = 0;
                    double systemDelta = 0;

                    // 1. Docker PreCPUStats
                    if (stats.PreCPUStats?.CPUUsage != null && stats.PreCPUStats.CPUUsage.TotalUsage > 0 && stats.PreCPUStats.SystemUsage > 0)
                    {
                        cpuDelta = currentCpu > stats.PreCPUStats.CPUUsage.TotalUsage ? (currentCpu - stats.PreCPUStats.CPUUsage.TotalUsage) : 0;
                        systemDelta = currentSystem > stats.PreCPUStats.SystemUsage ? (currentSystem - stats.PreCPUStats.SystemUsage) : 0;
                    }
                    // 2. Önceki örnek önbelleği
                    else if (_prevCpuStats.TryGetValue(dockerContainerId, out var prev))
                    {
                        if (currentCpu >= prev.CpuTotal && currentSystem > prev.SystemTotal)
                        {
                            cpuDelta = currentCpu - prev.CpuTotal;
                            systemDelta = currentSystem - prev.SystemTotal;
                        }
                    }

                    _prevCpuStats[dockerContainerId] = (currentCpu, currentSystem, DateTime.UtcNow);

                    long onlineCpus = stats.CPUStats.OnlineCPUs > 0 
                        ? (long)stats.CPUStats.OnlineCPUs 
                        : (stats.CPUStats.CPUUsage?.PercpuUsage != null && stats.CPUStats.CPUUsage.PercpuUsage.Count > 0 
                            ? stats.CPUStats.CPUUsage.PercpuUsage.Count 
                            : Environment.ProcessorCount);
                    if (onlineCpus <= 0) onlineCpus = 1;

                    double cpuPercent = 0.0;
                    if (systemDelta > 0.0 && cpuDelta >= 0.0)
                    {
                        cpuPercent = (cpuDelta / systemDelta) * onlineCpus * 100.0;
                    }

                    ulong rawUsage = stats.MemoryStats?.Usage ?? 0;
                    ulong cache = 0;
                    if (stats.MemoryStats?.Stats != null)
                    {
                        if (stats.MemoryStats.Stats.TryGetValue("cache", out var c)) cache = c;
                        else if (stats.MemoryStats.Stats.TryGetValue("inactive_file", out var inf)) cache = inf;
                        else if (stats.MemoryStats.Stats.TryGetValue("total_inactive_file", out var tinf)) cache = tinf;
                    }
                    ulong usedMemory = rawUsage > cache ? rawUsage - cache : rawUsage;
                    ulong memLimit = stats.MemoryStats?.Limit ?? 0;
                    if (memLimit == 0 || memLimit > (1UL << 50)) 
                    {
                        memLimit = 1024UL * 1024UL * 1024UL * 8UL;
                    }

                    statsDto.CpuPercentage = Math.Round(Math.Clamp(cpuPercent, 0, 100.0 * onlineCpus), 2);
                    statsDto.MemoryUsageBytes = usedMemory;
                    statsDto.MemoryLimitBytes = memLimit;
                    statsDto.MemoryPercentage = memLimit > 0 ? Math.Round((usedMemory / (double)memLimit) * 100.0, 2) : 0;
                }), cts.Token);
        }
        catch
        {
        }

        return statsDto;
    }

    public async Task<IEnumerable<string>> GetContainerLogsAsync(string dockerContainerId, int tailLines = 100)
    {
        using var stream = await _dockerClient.Containers.GetContainerLogsAsync(dockerContainerId, false,
            new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Tail = tailLines.ToString()
            });

        var result = await stream.ReadOutputToEndAsync(CancellationToken.None);
        var combined = (result.stdout + "\n" + result.stderr)
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .ToList();

        return combined;
    }

    public async Task<bool> IsContainerRunningAsync(string dockerContainerId)
    {
        if (string.IsNullOrWhiteSpace(dockerContainerId)) return false;
        using var cts = new CancellationTokenSource(2000);
        try
        {
            var inspect = await _dockerClient.Containers.InspectContainerAsync(dockerContainerId, cts.Token);
            return inspect?.State?.Running ?? false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> WaitForContainerHealthAsync(string dockerContainerId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var inspect = await _dockerClient.Containers.InspectContainerAsync(dockerContainerId, cancellationToken);
                if (inspect.State?.Running != true) return false;
                var health = inspect.State.Health?.Status;
                if (string.Equals(health, "healthy", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(health)) return true;
                if (string.Equals(health, "unhealthy", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        return false;
    }

    public async Task DeleteImageAsync(string imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName)) return;
        await _dockerClient.Images.DeleteImageAsync(imageName, new ImageDeleteParameters { Force = false, NoPrune = true });
    }
}
