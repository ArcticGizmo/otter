# Changelog

All notable changes to Otter are recorded here. Otter follows [semantic versioning](https://semver.org).

## [Unreleased]

---

## [v0.2.1] - 2026-06-26

- **Check for updates** — from the tray menu or the About page; Otter downloads the latest release and restarts itself
- **Changelog** — a new settings page showing what changed in each release

---

## [v0.2.0] - 2026-06-26

- Slack connections now keep themselves alive — Otter refreshes the access token in the background, so a long-running session no longer drops after a few hours
- Your stored Slack token is now encrypted at rest with Windows DPAPI

---

## [v0.1.0] - 2026-06-25

The first polished release — a ground-up visual revamp on top of the original Teams-call → Slack-status core.

- **Dark design system** — a single shared theme drives every surface, with custom-painted toggles and a spinner
- **First-class settings window** — a resizable dark window with a left navigation rail and pages for Getting started, Integrations (Microsoft Teams), Snooze, and About, replacing the old cramped dialog
- **Live status preview** — see exactly how your Slack status will look as you type, with workspace emoji autocomplete
- **Polished tray** — a themed right-click menu with a colour-coded status header; left-click the tray icon to open settings
- **Snooze** — pause Otter for a while straight from the tray or settings
- **Start at login** — flip one toggle and Otter is ready every time you sign in
- **Secure Slack sign-in** — OAuth with PKCE against a hosted callback, with everything stored locally
- **App icon** — Otter now has a proper otter mark for the taskbar, window, and About page
