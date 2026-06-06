# Codebase Analysis Report - DockerPanel

## 1. Executive Summary
This report presents a codebase analysis of the **DockerPanel** (ApiHub) solution. The system is designed as a self-hosted control panel for managing Docker containers, native processes, databases, Nginx reverse proxy routes, and mail servers. 

All core backend services and client interfaces (including Web and Mobile targets) are already fully implemented, despite some documentation files labeling certain features (such as push notifications and APK updates) as "in progress".

---

## 2. Project Solution & Structure
The solution consists of five main projects structured according to Clean Architecture principles:

*   **`DockerPanel.Domain`**: Pure C# domain library containing entities, enums, interfaces, and shared security validation helpers (`InputValidator.cs`). It has zero dependencies on other layers.
*   **`DockerPanel.Infrastructure`**: Contains the EF Core data context (`DockerPanelDbContext.cs`), migrations, and concrete service implementations handling low-level OS operations (Docker DotNet client, process starting, SSH execution, database provisioning, and ZIP extraction).
*   **`DockerPanel.API`**: ASP.NET Core REST API serving as the backend entry point. Contains controllers, hubs (`MetricLogHub.cs` for SignalR), configuration, and background workers (`MetricBackgroundWorker.cs`, `BackupWorker.cs`).
*   **`DockerPanel.Client`**: Blazor WebAssembly SPA frontend using MudBlazor for a cyber-emerald and ruby theme. Handles state provider auth and custom delegating handlers for JWT injection.
*   **`DockerPanel.Mobile`**: .NET MAUI Blazor Hybrid mobile client targeting Android. Integrates shared razor components from the client project, uses native `SecureStorage` for tokens, and configures FCM services.

---

## 3. Core Services Logic (DockerPanel.Infrastructure)

### A. `ProjectContainerService.cs`
*   **Docker API Connection**: Establishes a connection to the Docker daemon via Named Pipes on Windows (`npipe://./pipe/docker_engine`) and Unix sockets on Linux (`unix:///var/run/docker.sock`).
*   **Input Validation**: Performs command injection validation on container names using regex.
*   **Image Handling**: Searches local repo tags; pulls the requested image asynchronously from Docker Hub if not found.
*   **Networking**: Creates/attaches containers to an isolated bridge network (`dockerpanel-global-net`).
*   **Metrics**: Queries Docker stats stream with a 3-second timeout (`GetContainerStatsAsync`) returning single-frame metrics.

### B. `ProcessManagerService.cs`
*   **Concurrency Lock**: Uses a static `SemaphoreSlim(1,1)` to write thread-safe modifications to `/etc/project-manager/projects.conf`.
*   **Auto-Detection**: Scans native project directories for a `.runtimeconfig.json` corresponding to a DLL to identify the executable entry point.
*   **Shell Orchestration**: Spawns sudo sub-processes executing `/usr/local/bin/project-manager.sh` to start, stop, restart, or get status of processes.

### C. `ProjectZipDeployService.cs`
*   **Zip Slip Mitigation**: Computes target file paths with `Path.GetFullPath` and asserts that they start with the absolute path of the destination project directory, throwing `InvalidOperationException` if a traversal escape attempt is detected.
*   **Cleanup**: Safely clears read-only attributes on previous target files before deletion, with a fallback bash shell command deletion script.

### D. `DatabaseService.cs`
*   **Security**: Sanitizes DB names and usernames using database identifier regex checking to prevent SQL injection.
*   **AutoCommit Connection**: Spawns a secondary connection to PostgreSQL since database creation commands cannot run inside EF transaction blocks.
*   **Connection Termination**: Kills active sessions on target databases with `pg_terminate_backend` prior to dropping schemas.

### E. `BackupService.cs`
*   **Reliability**: Executes `pg_dump` through pipefail shell parameters inside the active Postgres container.
*   **Archiving**: Archives databases, native project folders, Nginx sites, and mail volumes into `tar.gz`.
*   **VDS Synchronization**: Generates and coordinates 4096-bit RSA keys for syncing local backups to remote VDS systems using SSH/SCP.

---

## 4. Docker Compose & Deployment Setup
The `docker-compose.yml` configures two persistent infrastructure services:
1.  **`db`**: Runs PostgreSQL 16 on Alpine, maps the PostgreSQL port `5432` to localhost, and writes database data to the local volume `pgdata`.
2.  **`mailserver`**: Runs `mailserver/docker-mailserver:latest` configured with standard mail ports (`25`, `143`, `587`, `993`) and mounts three subfolders on `/opt/dockerpanel/mail/` (data, state, config).
*   **Networks**: Both services bind to `dockerpanel-global-net` (defined as external in docker-compose.yml), isolating other containers.

---

## 5. EF Core Database & Entity Relationships

The schema consists of the following key tables and relations mapping in `DockerPanelDbContext.cs`:

*   **`User`**: One-to-many relationships with `Project`, `Subdomain`, `DnsRecord`, `DatabaseSchema`, `MailAccount`, `RootDomain`, `AuditLog`, `DeviceToken`, and `PushNotification`.
*   **`Project`**: Has one-to-many relations with `Subdomain`, `DnsRecord`, and `DatabaseSchema`. Features Cascade/SetNull delete behaviors.
*   **Unique Constraints**:
    *   `Users.Username` (Unique)
    *   `Projects.Name` (Unique)
    *   `Subdomains` (Unique index on `SubdomainName` + `DomainName` pair)
    *   `DatabaseSchemas.DbName` and `DbUser` (Unique)
    *   `MailAccounts.EmailAddress` (Unique)
    *   `RootDomains.Name` (Unique)
    *   `DeviceTokens.Token` (Unique)

---

## 6. Documentation Discrepancies, Gaps, and Outdated Details

| Document | Finding / Discrepancy / Gap |
| :--- | :--- |
| **`sunucu.md`** | **Critical Gap**: This document is completely empty (0 bytes). It should cover server configurations, user groups (`dockerpanel_api`, `adm`, `www-data`), shell scripts, and system requirements. |
| **`AGENTS.md`** | **Outdated Schema & Statuses**: <br>1. Section 3.B (Projects Table) misses the actual database columns `StartedAt` (DateTimeOffset?) and `EnablePhp` (boolean) which are defined in `Project.cs` and DbContext.<br>2. Lists APK download, shortcuts, and FCM services as conceptual goals, whereas they are fully implemented in the code. |
| **`mobil uygulama.md`** | **Outdated Statuses**: Marks FCM Push Notifications, APK distribution/auto-updates, and App Shortcuts & Deep Linking as "Developing" (🔄), but the actual code repository contains operational implementations of `FirebaseMessagingService.cs`, `AutoUpdateService.cs`, `DeepLinkService.cs`, and `DownloadsController.cs`. |
| **`ARCHITECTURE.md`** | **Minor Inconsistencies**: The architecture guide does not explicitly state that `StartedAt` and `EnablePhp` were introduced into the database mapping during later migration runs (e.g. `20260526221000_AddStartedAtToProjects` and `20260528004500_AddEnablePhpToProjects`). |
| **`RECOVERY_GUIDE.md`** | **Consistency**: Matches the actual `/opt/dockerpanel/restore-all.sh` shell restore automation logic perfectly. |
