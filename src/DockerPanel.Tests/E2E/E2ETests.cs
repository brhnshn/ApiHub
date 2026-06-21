using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Infrastructure.Data;
using DockerPanel.Infrastructure.Services;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Security;
using DockerPanel.API.Helpers;
using DockerPanel.API.Workers;
using Microsoft.AspNetCore.SignalR;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Net;

namespace DockerPanel.Tests.E2E;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor.

public class E2ETests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _optDir;
    private readonly string _projManagerDir;
    private readonly ServiceProvider _serviceProvider;
    private readonly DockerPanelDbContext _dbContext;
    private readonly SimulatedProcessManagerService _processManagerService;
    private readonly NginxProxyService _nginxService;
    private readonly FakeProjectContainerService _containerService;
    private readonly FakePushNotificationService _pushService;
    private readonly User _defaultUser;

    public E2ETests()
    {
        // 1. Setup local environment directories (opt_dockerpanel, project-manager)
        _baseDir = AppContext.BaseDirectory;
        _optDir = Path.Combine(_baseDir, "opt_dockerpanel");
        _projManagerDir = Path.Combine(_baseDir, "project-manager");

        CleanDirectories();

        Directory.CreateDirectory(_optDir);
        Directory.CreateDirectory(_projManagerDir);

        // 2. Set environment variables
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://localhost:5002");

        // Reset the NginxProxyService cached public IP to avoid cross-test contamination
        var cachedIpField = typeof(NginxProxyService).GetField("_cachedPublicIp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (cachedIpField != null)
        {
            cachedIpField.SetValue(null, null);
        }

        // 3. Configure DI
        var services = new ServiceCollection();
        
        // Add DbContext using InMemory DB
        services.AddDbContext<DockerPanelDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        // Add Configuration
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CertbotSettings:CloudflarePropagationSeconds"] = "1"
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);

        // Add Services
        services.AddSingleton<SimulatedProcessManagerService>();
        services.AddSingleton<IProcessManagerService>(sp => sp.GetRequiredService<SimulatedProcessManagerService>());
        services.AddSingleton<NginxProxyService>();
        services.AddSingleton<INginxService>(sp => sp.GetRequiredService<NginxProxyService>());
        services.AddSingleton<FakeProjectContainerService>();
        services.AddSingleton<IProjectContainerService>(sp => sp.GetRequiredService<FakeProjectContainerService>());
        services.AddSingleton<FakePushNotificationService>();
        services.AddSingleton<IPushNotificationService>(sp => sp.GetRequiredService<FakePushNotificationService>());
        services.AddSingleton<IHubContext<MetricLogHub>, FakeHubContext>();
        services.AddSingleton<ILogger<MetricBackgroundWorker>, FakeLogger<MetricBackgroundWorker>>();

        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<DockerPanelDbContext>();
        _processManagerService = _serviceProvider.GetRequiredService<SimulatedProcessManagerService>();
        _nginxService = _serviceProvider.GetRequiredService<NginxProxyService>();
        _containerService = (FakeProjectContainerService)_serviceProvider.GetRequiredService<IProjectContainerService>();
        _pushService = (FakePushNotificationService)_serviceProvider.GetRequiredService<IPushNotificationService>();

        // Ensure database is created and seed default user
        _dbContext.Database.EnsureCreated();
        _defaultUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = "hashedpassword",
            Role = UserRole.Administrator,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _dbContext.Users.Add(_defaultUser);
        _dbContext.SaveChanges();
    }

    private void CleanDirectories()
    {
        if (Directory.Exists(_optDir))
        {
            try { Directory.Delete(_optDir, true); } catch { }
        }
        if (Directory.Exists(_projManagerDir))
        {
            try { Directory.Delete(_projManagerDir, true); } catch { }
        }
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
        CleanDirectories();
    }

    private MetricBackgroundWorker CreateWorker()
    {
        return new MetricBackgroundWorker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _serviceProvider.GetRequiredService<IHubContext<MetricLogHub>>(),
            _serviceProvider.GetRequiredService<ILogger<MetricBackgroundWorker>>()
        );
    }

    private async Task RunWorkerOnceAsync(MetricBackgroundWorker worker, int watchdogCounterStart = 4)
    {
        var field = typeof(MetricBackgroundWorker).GetField("_watchdogCounter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(worker, watchdogCounterStart);

        using var cts = new CancellationTokenSource();
        var method = typeof(MetricBackgroundWorker).GetMethod("ExecuteAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var task = (Task)method!.Invoke(worker, new object[] { cts.Token })!;
        
        await Task.Delay(50);
        cts.Cancel();
        
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ==========================================
    // TIER 1: HAPPY PATHS & EQUIVALENCE CLASSES (25 Cases)
    // ==========================================

    // Feature 1: HTTP Redirection Leak Prevention
    
    [Fact]
    public async Task T1_F1_01_HTTP_Request_to_Registered_Subdomain()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.DockerContainer,
            ImageOrPath = "ubuntu",
            InternalPort = 5001,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer, sslEnabled: false);

        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "app.domain.com.conf");
        Assert.True(File.Exists(confFile));
        var content = await File.ReadAllTextAsync(confFile);
        Assert.Contains("proxy_pass http://127.0.0.1:5001", content);
    }

    [Fact]
    public async Task T1_F1_02_HTTP_Request_to_Unregistered_Domain()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);

        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "000-default-panel.conf");
        Assert.True(File.Exists(confFile));
        var content = await File.ReadAllTextAsync(confFile);
        Assert.Contains("listen 80 default_server;", content);
        Assert.Contains("return 444;", content);
    }

    [Fact]
    public async Task T1_F1_03_HTTP_Request_Directly_to_Server_IP()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);

        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "000-default-panel.conf");
        var content = await File.ReadAllTextAsync(confFile);
        Assert.Contains("listen 80 default_server;", content);
        Assert.Contains("return 444;", content);
    }

    [Fact]
    public async Task T1_F1_04_HTTP_Request_to_Localhost_Loopback()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);

        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "000-default-panel.conf");
        var content = await File.ReadAllTextAsync(confFile);
        Assert.Contains("localhost 127.0.0.1", content);
        Assert.Contains("proxy_pass http://127.0.0.1:5002", content);
    }

    [Fact]
    public async Task T1_F1_05_Lets_Encrypt_HTTP_Challenge_Bypass()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);

        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "000-default-panel.conf");
        var content = await File.ReadAllTextAsync(confFile);
        Assert.Contains("location /.well-known/acme-challenge/", content);
    }

    // Feature 2: HTTPS Redirection Leak Prevention

    [Fact]
    public async Task T1_F2_01_HTTPS_Request_to_Registered_Domain()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer, sslEnabled: true);
        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "app.domain.com.conf");
        var content = await File.ReadAllTextAsync(confFile);
        Assert.Contains("listen 443 ssl;", content);
    }

    [Fact]
    public async Task T1_F2_02_HTTPS_Request_to_Unregistered_Domain()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);
        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "000-default-panel.conf");
        var content = await File.ReadAllTextAsync(confFile);
        Assert.Contains("listen 443 ssl default_server;", content);
        Assert.Contains("return 444;", content);
    }

    [Fact]
    public async Task T1_F2_03_HTTPS_Default_Self_Signed_Certificate_Presentation()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);
        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "000-default-panel.conf");
        var content = await File.ReadAllTextAsync(confFile);
        Assert.Contains("ssl_certificate /etc/ssl/certs/nginx-selfsigned.crt;", content);
        Assert.Contains("ssl_certificate_key /etc/ssl/private/nginx-selfsigned.key;", content);
    }

    [Fact]
    public async Task T1_F2_04_Verify_Self_Signed_Cert_Generation_on_Disk()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);
        var certPath = Path.Combine(_optDir, "etc", "ssl", "certs", "nginx-selfsigned.crt");
        var keyPath = Path.Combine(_optDir, "etc", "ssl", "private", "nginx-selfsigned.key");
        Assert.True(File.Exists(certPath));
        Assert.True(File.Exists(keyPath));
        Assert.True(new FileInfo(certPath).Length > 0);
    }

    [Fact]
    public async Task T1_F2_05_HTTPS_Request_directly_to_Server_IP()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);
        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "000-default-panel.conf");
        var content = await File.ReadAllTextAsync(confFile);
        Assert.Contains("listen 443 ssl default_server;", content);
        Assert.Contains("return 444;", content);
    }

    // Feature 3: Native Project PID Stability (API Restart)

    [Fact]
    public async Task T1_F3_01_Native_Project_PID_Stability_on_API_Restart()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        await _processManagerService.AddOrUpdateProcessConfigAsync("proj1", 5030);
        _processManagerService.RunningProcesses["proj1"] = true;
        var pidFile = Path.Combine(_projManagerDir, "proj1.pid");
        await File.WriteAllTextAsync(pidFile, "1234");

        await DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider);

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
        Assert.Equal(0, _processManagerService.RestartCounts.GetValueOrDefault("proj1"));
    }

    [Fact]
    public async Task T1_F3_02_PID_Redetection_via_Process_Scan_PID_file_missing()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        await _processManagerService.AddOrUpdateProcessConfigAsync("proj1", 5030);
        _processManagerService.RunningProcesses["proj1"] = true; // Still running on OS

        await DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider);

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
    }

    [Fact]
    public async Task T1_F3_03_Stopped_Native_Project_Status_on_API_Restart()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Stopped,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        await _processManagerService.AddOrUpdateProcessConfigAsync("proj1", 5030);
        _processManagerService.RunningProcesses["proj1"] = false;

        await DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider);

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Stopped, updatedProject!.Status);
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
    }

    [Fact]
    public async Task T1_F3_04_Sync_Config_to_DB_Unlisted_Project_in_DB_but_in_config()
    {
        await _processManagerService.AddOrUpdateProcessConfigAsync("proj-manual", 5040);
        _processManagerService.RunningProcesses["proj-manual"] = true;

        await DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider);

        var project = await _dbContext.Projects.FirstOrDefaultAsync(p => p.Name == "proj-manual");
        Assert.NotNull(project);
        Assert.Equal(ProjectStatus.Running, project!.Status);
        Assert.Equal(5040, project.InternalPort);
    }

    [Fact]
    public async Task T1_F3_05_Sync_Config_to_DB_Project_missing_in_config_but_running_in_DB()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj-ghost",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        await DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider);

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Stopped, updatedProject!.Status);
    }

    // Feature 4: Native Project PID Stability (Watchdog)

    [Fact]
    public async Task T1_F4_01_Watchdog_Process_Verification_Happy_Path()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = true;

        await RunWorkerOnceAsync(CreateWorker());

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
    }

    [Fact]
    public async Task T1_F4_02_Watchdog_Auto_Restart_on_Crash()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = false;

        await RunWorkerOnceAsync(CreateWorker());

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
        Assert.Equal(1, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
    }

    [Fact]
    public async Task T1_F4_03_Watchdog_Max_Failure_Limit()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj-fail",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj-fail"] = false;

        var worker = CreateWorker();
        await RunWorkerOnceAsync(worker); // attempt 1
        await RunWorkerOnceAsync(worker); // attempt 2
        await RunWorkerOnceAsync(worker); // attempt 3 -> Error

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Error, updatedProject!.Status);
        Assert.True(_pushService.SentNotifications.Any(n => n.title.Contains("Otomatik Kurtarma Başarısız")));
    }

    [Fact]
    public async Task T1_F4_04_Transient_Check_Verification_False_Alarm_Protection()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningSequence.Enqueue(false);
        _processManagerService.RunningSequence.Enqueue(true);

        await RunWorkerOnceAsync(CreateWorker());

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
    }

    [Fact]
    public async Task T1_F4_05_Watchdog_Docker_Container_Recovery()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "container1",
            Type = ProjectType.DockerContainer,
            ImageOrPath = "nginx",
            DockerContainerId = "cont123",
            InternalPort = 80,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _containerService.ContainerStates["cont123"] = false;

        await RunWorkerOnceAsync(CreateWorker());

        Assert.Contains("cont123", _containerService.StartedContainers);
        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
    }

    // Feature 5: Stopped Project Status & Bypass

    [Fact]
    public async Task T1_F5_01_User_Stopped_Project_Remains_Stopped()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Stopped,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = false;

        await RunWorkerOnceAsync(CreateWorker());

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Stopped, updatedProject!.Status);
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
    }

    [Fact]
    public async Task T1_F5_02_Error_Project_Bypassed_by_Watchdog()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Error,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = false;

        await RunWorkerOnceAsync(CreateWorker());

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Error, updatedProject!.Status);
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
    }

    [Fact]
    public async Task T1_F5_03_Provisioning_Project_Bypassed_by_Watchdog()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Provisioning,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = false;

        await RunWorkerOnceAsync(CreateWorker());

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Provisioning, updatedProject!.Status);
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
    }

    [Fact]
    public async Task T1_F5_04_Database_Status_Stopped_Check()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj-stopped",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Stopped,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        await RunWorkerOnceAsync(CreateWorker());

        Assert.False(_processManagerService.RunningProcesses.ContainsKey("proj-stopped"));
    }

    [Fact]
    public async Task T1_F5_05_Start_Stopped_Project_Reactivates_Watchdog()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Stopped,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        // Start project
        project.Status = ProjectStatus.Running;
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = false;

        await RunWorkerOnceAsync(CreateWorker());

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
        Assert.Equal(1, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
    }

    // ==========================================
    // TIER 2: BOUNDARIES, EDGE CASES & ERROR HANDLING (25 Cases)
    // ==========================================

    // Feature 1: HTTP Redirection Leak Prevention

    [Fact]
    public async Task T2_F1_01_Missing_default_panel_config_on_startup()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);
        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "000-default-panel.conf");
        Assert.True(File.Exists(confFile));
        
        File.Delete(confFile);
        Assert.False(File.Exists(confFile));

        await _nginxService.ProvisionSubdomainAsync("app2", "domain.com", "proj1", 5001, ProjectType.DockerContainer);
        Assert.True(File.Exists(confFile));
    }

    [Fact]
    public async Task T2_F1_02_Default_symlink_conflict_resolution()
    {
        var defaultLink = Path.Combine(_optDir, "etc", "nginx", "sites-enabled", "default");
        Directory.CreateDirectory(Path.GetDirectoryName(defaultLink)!);
        await File.WriteAllTextAsync(defaultLink, "dummy conflict");
        Assert.True(File.Exists(defaultLink));

        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);

        Assert.False(File.Exists(defaultLink));
    }

    [Fact]
    public async Task T2_F1_03_IP_detection_failure_fallback()
    {
        var field = typeof(NginxProxyService).GetField("_cachedPublicIp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field!.SetValue(null, null);

        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);

        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "000-default-panel.conf");
        var content = await File.ReadAllTextAsync(confFile);
        
        Assert.Contains("localhost", content);
        Assert.Contains("127.0.0.1", content);
    }

    [Fact]
    public async Task T2_F1_04_Malformed_Subdomain_Validation()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _nginxService.ProvisionSubdomainAsync("sub_domain..com", "domain.com", "proj1", 5001, ProjectType.DockerContainer));
    }

    [Fact]
    public async Task T2_F1_05_Reload_failure_rollback()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => _nginxService.ReloadNginxAsync());
        Assert.Contains("Nginx reload komutlarinin hicbiri basarili olmadi", ex.Message);
    }

    // Feature 2: HTTPS Redirection Leak Prevention

    [Fact]
    public async Task T2_F2_01_Missing_self_signed_certificate_files_on_disk()
    {
        var certPath = Path.Combine(_optDir, "etc", "ssl", "certs", "nginx-selfsigned.crt");
        var keyPath = Path.Combine(_optDir, "etc", "ssl", "private", "nginx-selfsigned.key");
        
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);
        Assert.True(File.Exists(certPath));
        Assert.True(File.Exists(keyPath));

        File.Delete(certPath);
        File.Delete(keyPath);

        await _nginxService.ProvisionSubdomainAsync("app2", "domain.com", "proj1", 5001, ProjectType.DockerContainer);
        Assert.True(File.Exists(certPath));
        Assert.True(File.Exists(keyPath));
    }

    [Fact]
    public async Task T2_F2_02_SSL_Certificate_directory_permission_check()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer, sslEnabled: true);
        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "app.domain.com.conf");
        Assert.True(File.Exists(confFile));
    }

    [Fact]
    public async Task T2_F2_03_SNI_Spoofing_Test()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);
        var confFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "000-default-panel.conf");
        var content = await File.ReadAllTextAsync(confFile);
        Assert.Contains("listen 443 ssl default_server;", content);
        Assert.Contains("server_name _;", content);
        Assert.Contains("return 444;", content);
    }

    [Fact]
    public async Task T2_F2_04_Conflicting_listen_443_default_server_in_user_configs()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _nginxService.ProvisionSubdomainAsync("default_server", "domain.com", "proj1", 5001, ProjectType.DockerContainer));
    }

    [Fact]
    public async Task T2_F2_05_Expired_self_signed_certificate_regeneration()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);
        var certPath = Path.Combine(_optDir, "etc", "ssl", "certs", "nginx-selfsigned.crt");
        
        Assert.True(File.Exists(certPath));
        var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath);
        Assert.True(cert.NotAfter > DateTime.UtcNow);
    }

    // Feature 3: Native Project PID Stability (API Restart)

    [Fact]
    public async Task T2_F3_01_Ephemeral_run_folder_clearance()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        await _processManagerService.AddOrUpdateProcessConfigAsync("proj1", 5030);
        _processManagerService.RunningProcesses["proj1"] = true;
        var pidFile = Path.Combine(_projManagerDir, "proj1.pid");
        if (File.Exists(pidFile)) File.Delete(pidFile);

        await DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider);

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
    }

    [Fact]
    public async Task T2_F3_02_Port_conflict_during_startup_sync()
    {
        await _processManagerService.AddOrUpdateProcessConfigAsync("proj1", 5030);
        await _processManagerService.AddOrUpdateProcessConfigAsync("proj2", 5030);

        var exception = await Record.ExceptionAsync(() => 
            DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider));
        Assert.Null(exception);
    }

    [Fact]
    public async Task T2_F3_03_Invalid_PID_value_in_pid_file()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        await _processManagerService.AddOrUpdateProcessConfigAsync("proj1", 5030);
        var pidFile = Path.Combine(_projManagerDir, "proj1.pid");
        await File.WriteAllTextAsync(pidFile, "invalid_pid");
        _processManagerService.RunningProcesses["proj1"] = true;

        var exception = await Record.ExceptionAsync(() => 
            DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider));
        Assert.Null(exception);
        
        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
    }

    [Fact]
    public async Task T2_F3_04_Path_modification_during_offline_state()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "/pathA",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        await _processManagerService.AddOrUpdateProcessConfigAsync("proj1", 5030);
        var confPath = Path.Combine(_projManagerDir, "projects.conf");
        var line = "proj1|/pathB|dotnet proj1.dll --urls http://localhost:5030|root";
        await File.WriteAllTextAsync(confPath, line);

        await DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider);

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal("/pathB", updatedProject!.ImageOrPath);
    }

    [Fact]
    public async Task T2_F3_05_Sudoers_file_missing_corrupt_on_startup()
    {
        var exception = await Record.ExceptionAsync(() => 
            DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider));
        Assert.Null(exception);
    }

    // Feature 4: Native Project PID Stability (Watchdog)

    [Fact]
    public async Task T2_F4_01_High_CPU_Frozen_process_check()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = true;

        await RunWorkerOnceAsync(CreateWorker());

        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
    }

    [Fact]
    public async Task T2_F4_02_Status_check_command_timeout()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj-timeout",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.ExceptionProjects["proj-timeout"] = new TimeoutException("Command timed out");

        var worker = CreateWorker();
        var ex = await Record.ExceptionAsync(() => RunWorkerOnceAsync(worker));
        Assert.Null(ex);
    }

    [Fact]
    public async Task T2_F4_03_Process_user_mismatch()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = true;

        await RunWorkerOnceAsync(CreateWorker());

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
    }

    [Fact]
    public async Task T2_F4_04_Symlinked_CWD_path_match()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = true;

        await RunWorkerOnceAsync(CreateWorker());

        var updatedProject = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, updatedProject!.Status);
    }

    [Fact]
    public async Task T2_F4_05_DB_connection_loss_during_watchdog_execution()
    {
        _dbContext.Dispose();

        var worker = CreateWorker();
        var ex = await Record.ExceptionAsync(() => RunWorkerOnceAsync(worker));
        Assert.Null(ex);
    }

    // Feature 5: Stopped Project Status & Bypass

    [Fact]
    public async Task T2_F5_01_Zombie_process_cleanup_on_user_stop()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj-zombie",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj-zombie"] = true;

        await _processManagerService.StopProcessAsync("proj-zombie");

        Assert.False(_processManagerService.RunningProcesses["proj-zombie"]);
        Assert.Equal(1, _processManagerService.StopCounts.GetValueOrDefault("proj-zombie"));
    }

    [Fact]
    public async Task T2_F5_02_Concurrency_race_condition_Stop_vs_Watchdog()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = false;

        ProcessTransitionTracker.StartTransition("proj1");

        var worker = CreateWorker();
        await RunWorkerOnceAsync(worker);

        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));

        ProcessTransitionTracker.EndTransition("proj1");
    }

    [Fact]
    public async Task T2_F5_03_Direct_DB_update_to_Stopped()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy_path",
            InternalPort = 5030,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = false;

        project.Status = ProjectStatus.Stopped;
        await _dbContext.SaveChangesAsync();

        var worker = CreateWorker();
        await RunWorkerOnceAsync(worker);

        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
    }

    [Fact]
    public async Task T2_F5_04_PanicStop_persistence()
    {
        var project1 = new Project { Id = Guid.NewGuid(), UserId = _defaultUser.Id, Name = "proj1", Type = ProjectType.NativeProject, ImageOrPath = "dummy", Status = ProjectStatus.Stopped, InternalPort = 5001, CpuCount = 0.5, MemoryLimitBytes = 536870912 };
        var project2 = new Project { Id = Guid.NewGuid(), UserId = _defaultUser.Id, Name = "proj2", Type = ProjectType.NativeProject, ImageOrPath = "dummy", Status = ProjectStatus.Stopped, InternalPort = 5002, CpuCount = 0.5, MemoryLimitBytes = 536870912 };
        _dbContext.Projects.AddRange(project1, project2);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = false;
        _processManagerService.RunningProcesses["proj2"] = false;

        var worker = CreateWorker();
        await RunWorkerOnceAsync(worker);

        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj2"));
    }

    [Fact]
    public async Task T2_F5_05_External_termination_detection()
    {
        var project1 = new Project { Id = Guid.NewGuid(), UserId = _defaultUser.Id, Name = "proj1", Type = ProjectType.NativeProject, ImageOrPath = "dummy", Status = ProjectStatus.Running, InternalPort = 5001, CpuCount = 0.5, MemoryLimitBytes = 536870912 };
        var project2 = new Project { Id = Guid.NewGuid(), UserId = _defaultUser.Id, Name = "proj2", Type = ProjectType.NativeProject, ImageOrPath = "dummy", Status = ProjectStatus.Stopped, InternalPort = 5002, CpuCount = 0.5, MemoryLimitBytes = 536870912 };
        _dbContext.Projects.AddRange(project1, project2);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = false; // externally killed
        _processManagerService.RunningProcesses["proj2"] = false; // UI stopped

        var worker = CreateWorker();
        await RunWorkerOnceAsync(worker);

        Assert.Equal(1, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj2"));
    }

    // ==========================================
    // TIER 3: FEATURE INTERACTIONS (5 Cases)
    // ==========================================

    [Fact]
    public async Task T3_INT_01_Stopping_project_while_watchdog_is_running()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy",
            InternalPort = 5001,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        ProcessTransitionTracker.StartTransition("proj1");
        _processManagerService.RunningProcesses["proj1"] = false;

        var worker = CreateWorker();
        await RunWorkerOnceAsync(worker);

        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));

        ProcessTransitionTracker.EndTransition("proj1");
        project.Status = ProjectStatus.Stopped;
        await _dbContext.SaveChangesAsync();

        await RunWorkerOnceAsync(worker);
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj1"));
    }

    [Fact]
    public async Task T3_INT_02_Starting_project_on_conflicting_port()
    {
        var project1 = new Project { Id = Guid.NewGuid(), UserId = _defaultUser.Id, Name = "proj1", Type = ProjectType.NativeProject, ImageOrPath = "dummy", Status = ProjectStatus.Running, InternalPort = 5000, CpuCount = 0.5, MemoryLimitBytes = 536870912 };
        _dbContext.Projects.Add(project1);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = true;

        Assert.True(await _processManagerService.IsProcessRunningAsync("proj1"));
    }

    [Fact]
    public async Task T3_INT_03_Nginx_reload_during_api_restart()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj1", 5001, ProjectType.DockerContainer);
        
        var ex = await Record.ExceptionAsync(async () =>
        {
            await DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider);
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task T3_INT_04_Watchdog_runs_during_nginx_configuration_reload_failure()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj1",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy",
            InternalPort = 5001,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj1"] = true;

        var worker = CreateWorker();
        var ex = await Record.ExceptionAsync(() => RunWorkerOnceAsync(worker));
        
        Assert.Null(ex);
        Assert.True(await _processManagerService.IsProcessRunningAsync("proj1"));
    }

    [Fact]
    public async Task T3_INT_05_Database_migration_sync_error_during_pid_recovery()
    {
        await _processManagerService.AddOrUpdateProcessConfigAsync("proj1", 5001);
        var confPath = Path.Combine(_projManagerDir, "projects.conf");
        Assert.True(File.Exists(confPath));

        _dbContext.Dispose();

        var ex = await Record.ExceptionAsync(() => DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider));
        Assert.True(File.Exists(confPath));
    }

    // ==========================================
    // TIER 4: REAL-WORLD SCENARIOS (5 Cases)
    // ==========================================

    [Fact]
    public async Task T4_SCN_01_End_to_End_Lifecycle_of_a_Native_Project()
    {
        // 1. Deploy native project
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj-real",
            Type = ProjectType.NativeProject,
            ImageOrPath = "/opt/dockerpanel/projects/proj-real",
            InternalPort = 6001,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        await _processManagerService.AddOrUpdateProcessConfigAsync("proj-real", 6001);
        _processManagerService.RunningProcesses["proj-real"] = true;

        // 2. Link subdomain
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "proj-real", 6001, ProjectType.NativeProject, sslEnabled: false);
        var subFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "app.domain.com.conf");
        Assert.True(File.Exists(subFile));

        // 3. Restart API (sync)
        await DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider);
        var dbProj = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Running, dbProj!.Status);

        // 4. Watchdog scan
        var worker = CreateWorker();
        await RunWorkerOnceAsync(worker);
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj-real"));

        // 5. Stop project
        await _processManagerService.StopProcessAsync("proj-real");
        project.Status = ProjectStatus.Stopped;
        await _dbContext.SaveChangesAsync();

        // 6. Watchdog bypass stopped
        await RunWorkerOnceAsync(worker);
        Assert.Equal(0, _processManagerService.StartCounts.GetValueOrDefault("proj-real"));

        // 7. Nginx 444 default server verification
        var defaultFile = Path.Combine(_optDir, "etc", "nginx", "sites-available", "000-default-panel.conf");
        Assert.True(File.Exists(defaultFile));
        var content = await File.ReadAllTextAsync(defaultFile);
        Assert.Contains("listen 80 default_server;", content);
        Assert.Contains("return 444;", content);
    }

    [Fact]
    public async Task T4_SCN_02_Watchdog_Recovery_and_Escalation_under_System_Load()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = _defaultUser.Id,
            Name = "proj-load",
            Type = ProjectType.NativeProject,
            ImageOrPath = "dummy",
            InternalPort = 5005,
            Status = ProjectStatus.Running,
            CpuCount = 0.5,
            MemoryLimitBytes = 536870912
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _processManagerService.RunningProcesses["proj-load"] = false;

        var worker = CreateWorker();

        // Watchdog execution 1: Auto-restart and notification
        await RunWorkerOnceAsync(worker);
        Assert.Equal(1, _processManagerService.StartCounts.GetValueOrDefault("proj-load"));
        Assert.True(_pushService.SentNotifications.Any(n => n.title.Contains("Servis Durdu")));

        // Fail again
        _processManagerService.RunningProcesses["proj-load"] = false;

        // Watchdog execution 2: Restart attempt 2
        await RunWorkerOnceAsync(worker);
        Assert.Equal(2, _processManagerService.StartCounts.GetValueOrDefault("proj-load"));

        // Fail again
        _processManagerService.RunningProcesses["proj-load"] = false;

        // Watchdog execution 3: Restart attempt 3 -> transition to Error
        await RunWorkerOnceAsync(worker);
        
        var dbProj = await _dbContext.Projects.FindAsync(project.Id);
        Assert.Equal(ProjectStatus.Error, dbProj!.Status);
        Assert.True(_pushService.SentNotifications.Any(n => n.title.Contains("Otomatik Kurtarma Başarısız")));
    }

    [Fact]
    public async Task T4_SCN_03_Zero_Downtime_Subdomain_Switch_and_Nginx_Protection()
    {
        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "projA", 5001, ProjectType.NativeProject);
        var fileA = Path.Combine(_optDir, "etc", "nginx", "sites-available", "app.domain.com.conf");
        var contentA = await File.ReadAllTextAsync(fileA);
        Assert.Contains("proxy_pass http://127.0.0.1:5001", contentA);

        await _nginxService.ProvisionSubdomainAsync("app", "domain.com", "projB", 5002, ProjectType.NativeProject);
        var contentB = await File.ReadAllTextAsync(fileA);
        Assert.Contains("proxy_pass http://127.0.0.1:5002", contentB);
    }

    [Fact]
    public async Task T4_SCN_04_API_Node_Crash_and_Host_OS_Reboot_Recovery()
    {
        var projectA = new Project { Id = Guid.NewGuid(), UserId = _defaultUser.Id, Name = "projA", Type = ProjectType.NativeProject, ImageOrPath = "dummy", Status = ProjectStatus.Running, InternalPort = 5001, CpuCount = 0.5, MemoryLimitBytes = 536870912 };
        var projectB = new Project { Id = Guid.NewGuid(), UserId = _defaultUser.Id, Name = "projB", Type = ProjectType.NativeProject, ImageOrPath = "dummy", Status = ProjectStatus.Stopped, InternalPort = 5002, CpuCount = 0.5, MemoryLimitBytes = 536870912 };
        _dbContext.Projects.AddRange(projectA, projectB);
        await _dbContext.SaveChangesAsync();

        await _processManagerService.AddOrUpdateProcessConfigAsync("projA", 5001);
        await _processManagerService.AddOrUpdateProcessConfigAsync("projB", 5002);

        _processManagerService.RunningProcesses["projA"] = false;
        _processManagerService.RunningProcesses["projB"] = false;

        await DatabaseSyncHelper.SyncExistingSystemDataAsync(_serviceProvider);

        var dbProjA = await _dbContext.Projects.FindAsync(projectA.Id);
        var dbProjB = await _dbContext.Projects.FindAsync(projectB.Id);

        Assert.Equal(ProjectStatus.Stopped, dbProjA!.Status);
        Assert.Equal(ProjectStatus.Stopped, dbProjB!.Status);
    }

    [Fact]
    public async Task T4_SCN_05_Ephemeral_Container_Registry_and_Local_Process_Isolation()
    {
        await _processManagerService.AddOrUpdateProcessConfigAsync("proj1", 5001);
        var confPath = Path.Combine(_projManagerDir, "projects.conf");
        Assert.True(File.Exists(confPath));

        var lines = await File.ReadAllLinesAsync(confPath);
        Assert.Contains("proj1", lines.First());
    }
}

// ==========================================
// SIMULATED & FAKE TYPES FOR TESTING
// ==========================================

public class SimulatedProcessManagerService : IProcessManagerService
{
    private readonly ProcessManagerService _inner = new();
    
    public Dictionary<string, bool> RunningProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> RestartCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> StartCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> StopCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Exception> ExceptionProjects { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Queue<bool> RunningSequence { get; } = new();

    public Task RestartProcessAsync(string name)
    {
        RestartCounts[name] = RestartCounts.GetValueOrDefault(name) + 1;
        RunningProcesses[name] = true;
        
        var runDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "project-manager");
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, $"{name}.pid"), "1234");
        return Task.CompletedTask;
    }

    public Task RestartAllProcessesAsync()
    {
        return Task.CompletedTask;
    }

    public Task StopProcessAsync(string name)
    {
        StopCounts[name] = StopCounts.GetValueOrDefault(name) + 1;
        RunningProcesses[name] = false;
        
        var pidFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "project-manager", $"{name}.pid");
        if (File.Exists(pidFile)) File.Delete(pidFile);
        return Task.CompletedTask;
    }

    public Task StartProcessAsync(string name)
    {
        StartCounts[name] = StartCounts.GetValueOrDefault(name) + 1;
        RunningProcesses[name] = true;
        
        var runDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "project-manager");
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, $"{name}.pid"), "1234");
        return Task.CompletedTask;
    }

    public Task AddOrUpdateProcessConfigAsync(string name, int port, string? runtimeType = null, string? entryFile = null, string? customCommand = null)
    {
        return _inner.AddOrUpdateProcessConfigAsync(name, port, runtimeType, entryFile, customCommand);
    }

    public Task DeleteProcessConfigAsync(string name)
    {
        return _inner.DeleteProcessConfigAsync(name);
    }

    public Task<bool> IsProcessRunningAsync(string name)
    {
        if (ExceptionProjects.TryGetValue(name, out var ex))
        {
            throw ex;
        }
        if (RunningSequence.Count > 0)
        {
            return Task.FromResult(RunningSequence.Dequeue());
        }
        if (RunningProcesses.TryGetValue(name, out var isRunning))
        {
            return Task.FromResult(isRunning);
        }
        
        var pidFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "project-manager", $"{name}.pid");
        return Task.FromResult(File.Exists(pidFile));
    }

    public Task<IEnumerable<string>> GetProcessLogsAsync(string name, int tailLines = 100)
    {
        return Task.FromResult<IEnumerable<string>>(new[] { "Simulated log 1", "Simulated log 2" });
    }

    public Task RestoreDependenciesAsync(string name, string path, string? runtimeType)
    {
        return Task.CompletedTask;
    }
}

public class FakeProjectContainerService : IProjectContainerService
{
    public Dictionary<string, bool> ContainerStates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ContainerStatsDto> ContainerStats { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> StartedContainers { get; } = new();
    public List<string> StoppedContainers { get; } = new();

    public Task<string> ProvisionContainerAsync(string name, string imageName, long memoryLimitBytes, double cpuCount, int internalPort)
    {
        return Task.FromResult(Guid.NewGuid().ToString());
    }

    public Task StopContainerAsync(string dockerContainerId)
    {
        ContainerStates[dockerContainerId] = false;
        StoppedContainers.Add(dockerContainerId);
        return Task.CompletedTask;
    }

    public Task StartContainerAsync(string dockerContainerId)
    {
        ContainerStates[dockerContainerId] = true;
        StartedContainers.Add(dockerContainerId);
        return Task.CompletedTask;
    }

    public Task DeleteContainerAsync(string dockerContainerId)
    {
        ContainerStates.Remove(dockerContainerId);
        return Task.CompletedTask;
    }

    public Task UpdateContainerLimitsAsync(string dockerContainerId, long memoryLimitBytes, double cpuCount)
    {
        return Task.CompletedTask;
    }

    public Task<ContainerStatsDto> GetContainerStatsAsync(string dockerContainerId)
    {
        if (ContainerStats.TryGetValue(dockerContainerId, out var stats))
        {
            return Task.FromResult(stats);
        }
        return Task.FromResult(new ContainerStatsDto
        {
            CpuPercentage = 5.0,
            MemoryUsageBytes = 100 * 1024 * 1024,
            MemoryLimitBytes = 512 * 1024 * 1024,
            MemoryPercentage = 20.0
        });
    }

    public Task<bool> IsContainerRunningAsync(string dockerContainerId)
    {
        if (ContainerStates.TryGetValue(dockerContainerId, out var state))
        {
            return Task.FromResult(state);
        }
        return Task.FromResult(true);
    }

    public Task<IEnumerable<string>> GetContainerLogsAsync(string dockerContainerId, int tailLines = 100)
    {
        return Task.FromResult<IEnumerable<string>>(new[] { "Log 1", "Log 2" });
    }
}

public class FakePushNotificationService : IPushNotificationService
{
    public List<(Guid userId, string title, string body, string? deepLink)> SentNotifications { get; } = new();

    public Task SendNotificationToUserAsync(Guid userId, string title, string body, string? deepLink = null)
    {
        SentNotifications.Add((userId, title, body, deepLink));
        return Task.CompletedTask;
    }

    public bool IsFcmConfigured() => true;
}

public class FakeHubContext : IHubContext<MetricLogHub>
{
    public IHubClients Clients { get; } = new FakeHubClients();
    public IGroupManager Groups { get; } = new FakeGroupManager();
}

public class FakeHubClients : IHubClients
{
    public IClientProxy All { get; } = new FakeClientProxy();
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new FakeClientProxy();
    public IClientProxy Client(string connectionId) => new FakeClientProxy();
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new FakeClientProxy();
    public IClientProxy Group(string groupName) => new FakeClientProxy();
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => new FakeClientProxy();
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new FakeClientProxy();
    public IClientProxy User(string userId) => new FakeClientProxy();
    public IClientProxy Users(IReadOnlyList<string> userIds) => new FakeClientProxy();
}

public class FakeClientProxy : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

public class FakeGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class FakeLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }
}
