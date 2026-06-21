using Xunit;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using DockerPanel.Infrastructure.Services;
using DockerPanel.Domain.Enums;

namespace DockerPanel.Tests;

public class NginxProxyServiceTests
{
    private class DummyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public async Task ProvisionSubdomainAsync_ShouldGenerateSelfSignedCertAndCorrectConfig()
    {
        // Clean up any existing generated files before test to ensure real generation runs
        var baseDir = AppContext.BaseDirectory;
        var certPath = Path.Combine(baseDir, "opt_dockerpanel", "etc", "ssl", "certs", "nginx-selfsigned.crt");
        var keyPath = Path.Combine(baseDir, "opt_dockerpanel", "etc", "ssl", "private", "nginx-selfsigned.key");
        var configPath = Path.Combine(baseDir, "opt_dockerpanel", "etc", "nginx", "sites-available", "000-default-panel.conf");

        if (File.Exists(certPath)) File.Delete(certPath);
        if (File.Exists(keyPath)) File.Delete(keyPath);
        if (File.Exists(configPath)) File.Delete(configPath);

        // Arrange
        var serviceProvider = new DummyServiceProvider();
        var configuration = new ConfigurationBuilder().Build();
        var service = new NginxProxyService(serviceProvider, configuration);

        // Act
        await service.ProvisionSubdomainAsync(
            "testsub", 
            "example.com", 
            "test-container", 
            8080, 
            ProjectType.DockerContainer, 
            sslEnabled: false, 
            reloadNginx: false);

        // Assert
        Assert.True(File.Exists(certPath), "Self-signed certificate was not generated.");
        Assert.True(File.Exists(keyPath), "Self-signed key was not generated.");
        Assert.True(File.Exists(configPath), "Default panel config file was not generated.");

        // Read and verify config contents
        var configContent = await File.ReadAllTextAsync(configPath);
        
        // 1. Should have port 443 default_server block returning 444
        Assert.Contains("listen 443 ssl default_server;", configContent);
        Assert.Contains("listen [::]:443 ssl default_server;", configContent);
        Assert.Contains("return 444;", configContent);

        // 2. Should use default self signed cert paths in config
        Assert.Contains("ssl_certificate /etc/ssl/certs/nginx-selfsigned.crt;", configContent);
        Assert.Contains("ssl_certificate_key /etc/ssl/private/nginx-selfsigned.key;", configContent);

        // 3. Should have port 443 SSL server block for localhost, 127.0.0.1
        Assert.Contains("server_name localhost 127.0.0.1", configContent);
    }
}
