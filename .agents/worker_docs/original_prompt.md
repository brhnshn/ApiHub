## 2026-06-05T01:09:54Z
You are dispatched as a worker to update, clean, and translate the docs/ folder in c:\Users\sahin\Desktop\cpanelproje.
Please perform the following operations:

1. Update `docs/ARCHITECTURE.md` (keep in Turkish, update any details like adding StartedAt and EnablePhp columns to the project database representation, and ensure consistency).
2. Update `docs/AGENTS.md` (keep in Turkish, update Projects table schema to include StartedAt and EnablePhp, and mark mobile app features like FCM push, APK downloads, deep linking as completed).
3. Update `docs/mobil uygulama.md` (mark FCM Push, APK distribution, deep linking as fully completed in the scope table).
4. Create `docs/sunucu.md` with detailed information (in Turkish) about server requirements, dockerpanel_api system user setup, permission configurations (chmod 666 /var/run/docker.sock), sudoers configurations, directory permissions for logs, systemd service setup, and directory mappings.
5. Create `docs/kod_eksiklikleri_ve_iyileştirmeler.md` (in Turkish) highlighting code gaps and technical debts:
   - Absence of unit test projects (e.g. DockerPanel.Tests).
   - Local token security differences (localStorage in Web WASM vs SecureStorage in MAUI).
   - Hardcoded Firebase configs or credentials in source/appsettings.
   - Single-user design constraints.
   - Any other improvements.
6. Delete or empty out the corrupt `docs/pdf_text.txt` file (you can delete it or overwrite it with a blank/minimal message, or run a command to delete it).
7. Clean up or archive `docs/implementation_plan.md` and `docs/MULTIDOMAIN_PLAN.md` (e.g., mark all milestones as completed/implemented in Turkish).
8. Ensure all markdown files have clean formatting with no TODOs or placeholders.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A Forensic Auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Please write a handoff.md in your working directory (.agents/worker_docs/) once complete, and send a message back.
