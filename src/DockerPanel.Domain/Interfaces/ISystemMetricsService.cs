using System.Threading.Tasks;

namespace DockerPanel.Domain.Interfaces;

public class SystemMetricsResult
{
    public double Cpu { get; set; }
    public double RamPercentage { get; set; }
    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }
    public double DiskUsedPercentage { get; set; }
    public double DiskUsedGb { get; set; }
    public double DiskTotalGb { get; set; }
}

public interface ISystemMetricsService
{
    Task<double> GetSystemCpuUsageAsync();
    (double UsedPercentage, double UsedGb, double TotalGb) GetSystemRamUsage();
    (double UsedPercentage, double UsedGb, double TotalGb) GetSystemDiskUsage();
    Task<SystemMetricsResult> GetCurrentMetricsAsync();
}
