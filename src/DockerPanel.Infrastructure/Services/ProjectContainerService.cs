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

    public async Task<string> ProvisionContainerAsync(string name, string imageName, long memoryLimitBytes, double cpuCount, int internalPort)
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
        var containerPortKey = $"{internalPort}/tcp";
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
                    {
                        containerPortKey,
                        new List<PortBinding>
                        {
                            new()
                            {
                                HostIP = "127.0.0.1",
                                HostPort = internalPort.ToString()
                            }
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

        SystemLogQueue.Log("info", $"$ docker create --name {name} --net dockerpanel-global-net -m {memoryLimitBytes} --cpus {cpuCount} -p 127.0.0.1:{internalPort}:{internalPort}/tcp {fromImage}:{tag}");
        var response = await _dockerClient.Containers.CreateContainerAsync(config);
        SystemLogQueue.Log("info", $"[Docker] Konteyner oluşturuldu. ID: {response.ID.Substring(0, 12)}");

        // 5. Başlatma Lojiği
        SystemLogQueue.Log("info", $"$ docker start {name}");
        await _dockerClient.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
        SystemLogQueue.Log("info", $"[Docker] Konteyner başarıyla sağlandı ve arka planda çalıştırıldı (Running).");

        return response.ID;
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

        // Single metric read using stats stream cancellation
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(3000); // 3 saniye zaman aşımı

        try
        {
            await _dockerClient.Containers.GetContainerStatsAsync(dockerContainerId,
                new ContainerStatsParameters { Stream = false },
                new Progress<ContainerStatsResponse>(stats =>
                {
                    double cpuDelta = stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage;
                    double systemDelta = stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage;
                    
                    double cpuPercent = 0.0;
                    if (systemDelta > 0.0 && cpuDelta > 0.0)
                    {
                        cpuPercent = (cpuDelta / systemDelta) * stats.CPUStats.OnlineCPUs * 100.0;
                    }

                    ulong cache = 0;
                    if (stats.MemoryStats.Stats != null)
                    {
                        if (stats.MemoryStats.Stats.TryGetValue("cache", out var c)) cache = c;
                        else if (stats.MemoryStats.Stats.TryGetValue("inactive_file", out var inf)) cache = inf;
                    }
                    ulong usedMemory = stats.MemoryStats.Usage > cache ? stats.MemoryStats.Usage - cache : stats.MemoryStats.Usage;

                    statsDto.CpuPercentage = Math.Round(cpuPercent, 2);
                    statsDto.MemoryUsageBytes = usedMemory;
                    statsDto.MemoryLimitBytes = stats.MemoryStats.Limit;
                    statsDto.MemoryPercentage = Math.Round((usedMemory / (double)stats.MemoryStats.Limit) * 100.0, 2);
                }), cts.Token);
        }
        catch
        {
            // Zaman aşımı veya durmuş konteyner durumunda varsayılan 0 değerlerini döner
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
}
