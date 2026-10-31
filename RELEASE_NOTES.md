## [2026.10.0-port.23] - 2026-10-31

- Fixed shared WebView session preparation by sending the official bare Schoology app-domain value to the session-start endpoint.
- Replaced the custom drawer toggle with the WinUI TitleBar pane-toggle integration and a standard expanded left NavigationView.
- Fixed assignment detail visual-tree ownership by creating independent Info, Comments, and Submissions views for each responsive layout.
- Rendered message threads from the API's message collection instead of assuming a single message object.
- Expanded update attachment parsing for Android's nested files, links, videos, embeds, and thumbnail-backed image attachments; improved poll option wrapper handling.
- Made the title-bar Create command contextual to Home and course/group Updates, and connected text-only update posting to Schoology's updates endpoint.
- Kept the course/group/school infobox outside the animated tab content so it remains stationary while tabs change.

## [2026.10.0-port.22]

- Fix course-material assignment rows so they show only the assignment title, and route assignment IDs using the explicit assignment ID field when present.
- Fix message detail rendering so the plain-text `message` body is not mistaken for a nested response object.
- Render post images and file attachments from the official nested `attachments.files.file` structure.
- Read Schoology API responses as UTF-8 bytes so malformed response charset headers cannot break shared WebView session setup.
- Fix locale-sensitive formatting and the reported .NET analyzer warnings; retain the HMAC-SHA1 OAuth signature required by Schoology's OAuth 1.0a API.

## [2026.10.0-port.20]

- Add official Android-compatible poll voting in a native WinUI ListView.
- Add connected folder open/back animations that respect Windows and app reduced-motion settings.
- Reuse one avatar-backed comment row in post previews and the full comments dialog; show profile photos in likes.
- Fix the like/comment action divider so it sits between the action label and its count, not over the count.
- Size member lists to the available window viewport so their native ListView can scroll through the complete roster.

## [2026.10.0-port.19]

- Reduce Windows installer size by downloading the Windows App SDK runtime prerequisite during setup instead of embedding its installer inside every Schoology installer.
- Fix message detail retrieval to use the official inbox/sent thread endpoints and request message attachments.
- Scope course, group, and school-profile Upcoming panes to that selected entity.
- Add a native course Assignments list and native assignment detail surface, preserving LTI assignments as embedded web content.
- Add course applications to the course sidebar and launch them through the v2 course-app endpoint.
- Remove the standalone School Profile drawer destination while retaining the course-to-school profile link.
- Improve course infobox school links, remove redundant Course/Group labels in portrait, center Settings, and use the native WinUI ringer icon for Notifications.

## [2026.10.0-port.18]

- Fix WinUI 3 WebView2 environment initialization by using the supported `CreateWithOptionsAsync` API while retaining the persistent shared WebView data folder.

## [2026.10.0-port.17]

- Replace the course dashboard list with a responsive, virtualized WinUI GridView of Tauri-style course cards.
- Change the drawer Notifications icon to a bell.
- Embed and use the exact nine Schoology folder PNGs from Tauri port.28, selected from each course material's folder color.
- Add Windows App Notifications for newly received Schoology activity, with a five-minute background poll, first-run/account-switch baseline, and Tauri-matching optional notification sound.
- Add a Windows startup entry, hidden background launch, notification-area icon/menu, and single-instance activation so Schoology can remain running after its window is closed.
- Deploy the matching Windows App SDK runtime alongside the installer because App Notifications require Windows App SDK runtime services that are unavailable in a self-contained Windows App SDK deployment.
- Fix the reported compact-frame definite-assignment error and initialize WebView2 with an explicit CoreWebView2Environment instead of the unsupported CreationProperties property.

## [2026.10.0-port.16]

- Add native notifications, requests, and hierarchical resources pages using Schoology API data.
- Convert grades and resource browsing to expandable WinUI TreeViews.
- Add a persistent authenticated WebView2 for Schoology account info, assessment content, and LTI/resource-app launches.
- Replace generic empty provider placeholders with the Tauri port's empty-state wording.
- Fix the three reported C# compiler errors.

# Schoology WinUI release notes

## [2026.10.0-port.15]

### Fixed
- Fixed Schoology post likes to send the `like_action` form field in the POST body, matching the official Android client.
- Resolved Recent Activity authors from Schoology user profiles and render post body text instead of raw rich-text HTML.
- Added basic image attachment and poll-result rendering to shared post cards.
- Replaced the Upcoming placeholder with an interactive ListView grouped by date and added a date-selectable Calendar page.
- Added a native Settings page with persisted theme and reduced-motion preferences.
- Removed nested ScrollViewer wrappers from list-heavy course views and routed course dashboard, materials-folder, and message row activation directly through ListViewItem taps.
- Changed learner grade loading to the official `users/{user_id}/grades` endpoint and made each course expandable on demand.
- Hid the landscape entity infobox in portrait mode; portrait course details now include the subtitle and a school-profile route.
- Added a versioned GitHub Release publishing job for main-branch pushes and manual Actions runs.
- Updated installer branding to display the app name as Schoology.

### Notes
- Builds and installer creation remain delegated to GitHub Actions; no local builds or installer tests are run.
