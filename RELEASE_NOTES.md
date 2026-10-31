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
