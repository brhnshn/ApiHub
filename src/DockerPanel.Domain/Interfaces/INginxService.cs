using System.Threading.Tasks;
using DockerPanel.Domain.Enums;

namespace DockerPanel.Domain.Interfaces;

public interface INginxService
{
    Task ProvisionSubdomainAsync(string subdomainName, string domainName, string containerName, int containerPort, ProjectType projectType = ProjectType.DockerContainer, string? staticPath = null, bool? enablePhp = null, bool sslEnabled = false, bool reloadNginx = true);
    Task DeleteSubdomainAsync(string subdomainName, string domainName);
    Task EnableSslWithCertbotAsync(string subdomainName, string domainName);
    Task SyncActiveConfigsWithDbAsync(System.Guid userId);
    Task ReloadNginxAsync();
}

