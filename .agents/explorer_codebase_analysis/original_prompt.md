## 2026-06-05T01:07:55Z
Perform a codebase analysis of c:\Users\sahin\Desktop\cpanelproje.
Specifically:
1. Examine the project solution and structure: DockerPanel.Domain, DockerPanel.Infrastructure, DockerPanel.API, DockerPanel.Client, DockerPanel.Mobile.
2. Read the key service implementation files in DockerPanel.Infrastructure (e.g. ProjectContainerService.cs, ProcessManagerService.cs, ProjectZipDeployService.cs, DatabaseService.cs, BackupService.cs) to understand their logic.
3. Understand the docker-compose.yml configuration and deployment setup.
4. Analyze the database structure and entity relationships (EF Core).
5. Compare the actual code implementation with existing docs: ARCHITECTURE.md, AGENTS.md, RECOVERY_GUIDE.md, mobil uygulama.md, sunucu.md. Identify discrepancies, outdated details, or gaps.
6. Write your analysis report to .agents/explorer_codebase_analysis/analysis.md and also write a handoff.md in that directory.
7. Send a message to the orchestrator when done with a summary of the report.
