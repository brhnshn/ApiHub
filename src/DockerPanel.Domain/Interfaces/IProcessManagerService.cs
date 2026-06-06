using System.Collections.Generic;
using System.Threading.Tasks;

namespace DockerPanel.Domain.Interfaces;

public interface IProcessManagerService
{
    Task RestartProcessAsync(string name);
    Task RestartAllProcessesAsync();
    Task StopProcessAsync(string name);
    Task StartProcessAsync(string name);
    Task AddOrUpdateProcessConfigAsync(string name, int port, string? runtimeType = null, string? entryFile = null, string? customCommand = null);
    Task DeleteProcessConfigAsync(string name);
    Task<bool> IsProcessRunningAsync(string name);
    Task<IEnumerable<string>> GetProcessLogsAsync(string name, int tailLines = 100);
    Task RestoreDependenciesAsync(string name, string path, string? runtimeType);
}
