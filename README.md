# Schoology WinUI 3

A new native Windows app shell for Schoology, built with WinUI 3 and the Windows App SDK. This is a separate native project, not an Electron/Tauri wrapper.

## Current foundation

- Native WinUI 3 unpackaged, self-contained application with GitHub Actions builds for x64 and ARM64.
- Brief Schoology-blue splash screen, then a login entry screen.
- Login handoff to the official Schoology sign-in website; this first phase deliberately does not collect/store passwords or pretend that a browser session is already integrated.
- Home navigation drawer and dashboard layout for recent activity and courses.
- Upcoming page with a two-column upcoming/recent-activity split.
- Shared three-pane shell for courses, groups, and school profile.
- Light/dark semantic theme resources plus high-contrast resource dictionary.

## Important implementation status

This is the UI foundation only. Dashboard data is illustrative until the official Schoology Android source behavior and the port's API/authentication code have been ported to a native C# service layer. The login screen opens the official sign-in page and does not yet import its session. No credentials are stored. Course/group/school panes currently show clear integration placeholders instead of fabricated live data.

## Build

GitHub Actions restores, builds, publishes a self-contained WinUI app, verifies the executable exists, and creates a per-user installer for x64 and ARM64. The installer targets `%LOCALAPPDATA%\\Programs\\Schoology`, matching the current-user install directory used by the Tauri Schoology app. It installs `Schoology.WinUI.exe` alongside the existing `Schoology.exe` and gives the native app a separate Start menu shortcut; it does not replace the Tauri executable. The installer tracks the files it adds so uninstalling the WinUI app does not intentionally remove Tauri-owned files. No local Windows build or installer test was run.

## Reference sources

The UI layout reference is the supplied Schoology Desktop Port Tauri port.28 source archive, not the Android app layout. The official Schoology Android 2026.10.0 source remains a behavior/API reference for future native integration. The user-requested port.27 archive was not available, so port.28 is the concrete desktop layout reference.
