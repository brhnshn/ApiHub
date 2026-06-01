using System.Collections.Generic;
using System.Threading.Tasks;

namespace DockerPanel.Domain.Interfaces;

public class CloudflareDnsRecordDto
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Ttl { get; set; }
    public bool Proxied { get; set; }
}

public interface ICloudflareService
{
    Task<string> CreateDnsRecordAsync(string token, string zoneId, string type, string name, string content, bool proxied);
    Task DeleteDnsRecordAsync(string token, string zoneId, string cloudflareRecordId);
    Task<List<CloudflareDnsRecordDto>> ListDnsRecordsAsync(string token, string zoneId);
}
