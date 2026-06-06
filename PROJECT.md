# Project: DockerPanel Documentation and Refactoring Report

## Architecture
DockerPanel is a self-hosted panel managing Docker containers, databases, mail servers, and projects using .NET Web API and Blazor WASM.
- **DockerPanel.Domain**: Pure domain models, interfaces, and enums.
- **DockerPanel.Infrastructure**: Database (EF Core), services for Docker container management, SSH syncing, Process management, Database management.
- **DockerPanel.API**: REST API endpoints, SignalR hubs, and Background Workers.
- **DockerPanel.Client**: Blazor SPA client interface using MudBlazor.
- **DockerPanel.Mobile**: Mobile client application (MAUI or similar).

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| 1 | Codebase Exploration | Read the codebase, analyze the structure, and identify files/settings | None | DONE |
| 2 | Core Docs Update & Translation | Translate and update ARCHITECTURE.md, AGENTS.md, RECOVERY_GUIDE.md | M1 | DONE |
| 3 | Feature Docs Update & Translation | Translate and update mobil uygulama.md, sunucu.md, and perform file cleanup | M1 | DONE |
| 4 | Missing Docs Creation | Add installation steps, API reference, or new necessary guides | M1, M2 | DONE |
| 5 | Gaps & Improvements Report | Analyze code gaps and compile kod_eksiklikleri_ve_iyileştirmeler.md | M1 | DONE |
| 6 | Verification | Verify and review all changes in docs/ folder | M2, M3, M4, M5 | DONE |

## Code Layout
- `src/DockerPanel.Domain/`
- `src/DockerPanel.Infrastructure/`
- `src/DockerPanel.API/`
- `src/DockerPanel.Client/`
- `src/DockerPanel.Mobile/`
- `docs/`
