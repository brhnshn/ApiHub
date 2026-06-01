using System.IO;
using System.Threading.Tasks;

namespace DockerPanel.Domain.Interfaces;

public interface IProjectZipDeployService
{
    Task<string> DeployZipAsync(string name, Stream zipStream);
}
