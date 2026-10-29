# Schoology WinUI 3

A new native Windows app shell for Schoology, built with WinUI 3 and the Windows App SDK. This is a separate native project, not an Electron/Tauri wrapper.

## Current foundation

- Native WinUI 3 packaged application and Windows GitHub Actions workflow (x64 and ARM64).
- Brief Schoology-blue splash screen, then a login entry screen.
- Login handoff to the official Schoology sign-in website; this first phase deliberately does not collect/store passwords or pretend that a browser session is already integrated.
- Home navigation drawer and dashboard layout for recent activity and courses.
- Upcoming page with a two-column upcoming/recent-activity split.
- Shared three-pane shell for courses, groups, and school profile.
- Light/dark semantic theme resources plus high-contrast resource dictionary.

## Important implementation status

This is the UI foundation only. Dashboard data is illustrative until the official Schoology Android source behavior and the port's API/authentication code have been ported to a native C# service layer. The login screen opens the official sign-in page and does not yet import its session. No credentials are stored. Course/group/school panes currently show clear integration placeholders instead of fabricated live data.

## Build

GitHub Actions builds on `windows-latest`; no local Electron or installer build is required. The workflow uploads any generated MSIX outputs as artifacts. Packaging/signing configuration can be finalized after the first CI run reports the exact Windows App SDK packaging output for the repository.

## Reference sources

The initial layout is adapted from the supplied Schoology Desktop Port port.28 source archive and is intended to be reconciled with the official Schoology Android 2026.10.0 source. The user-requested port.27 archive was not among the files available in this task, so port.28 is the concrete layout reference used for this starter.
