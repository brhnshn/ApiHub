using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DockerPanel.Domain.Entities;

namespace DockerPanel.Domain.Interfaces;

public interface IProjectContainerService
{
    Task<string> ProvisionContainerAsync(string name, string imageName, long memoryLimitBytes, double cpuCount, int internalPort);
    Task StopContainerAsync(string dockerContainerId);
    Task StartContainerAsync(string dockerContainerId);
    Task DeleteContainerAsync(string dockerContainerId);
    Task UpdateContainerLimitsAsync(string dockerContainerId, long memoryLimitBytes, double cpuCount);
    Task<ContainerStatsDto> GetContainerStatsAsync(string dockerContainerId);
    Task<bool> IsContainerRunningAsync(string dockerContainerId);
    Task<IEnumerable<string>> GetContainerLogsAsync(string dockerContainerId, int tailLines = 100);
}
