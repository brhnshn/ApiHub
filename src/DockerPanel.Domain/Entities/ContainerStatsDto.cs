namespace DockerPanel.Domain.Entities;

/// <summary>
/// Docker konteyner istatistik verileri için veri transfer nesnesi (DTO).
/// Docker.DotNet'in ContainerStatsResponse modelinden dönüştürülür.
/// </summary>
public class ContainerStatsDto
{
    /// <summary>CPU kullanım yüzdesi (örn: 12.5 = %12.5)</summary>
    public double CpuPercentage { get; set; }

    /// <summary>Anlık bellek tüketimi (byte cinsinden)</summary>
    public double MemoryUsageBytes { get; set; }

    /// <summary>Konteynere atanan maksimum bellek limiti (byte cinsinden)</summary>
    public double MemoryLimitBytes { get; set; }

    /// <summary>Bellek doluluk yüzdesi (MemoryUsageBytes / MemoryLimitBytes * 100)</summary>
    public double MemoryPercentage { get; set; }
}
