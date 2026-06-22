using System.Collections.Generic;
using System.Threading.Tasks;
using DockerPanel.Domain.Entities;

namespace DockerPanel.Domain.Interfaces;

public interface IDatabaseService
{
    Task ProvisionDatabaseAsync(string dbName, string dbUser, string dbPassword);
    Task DeleteDatabaseAsync(string dbName, string dbUser);
    Task<long> GetDatabaseSizeAsync(string dbName);
    Task<List<ExistingDatabaseInfo>> DiscoverExistingDatabasesAsync();
    Task<int> GetActiveConnectionsCountAsync();
}
