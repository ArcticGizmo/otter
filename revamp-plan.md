# Otter Revamp Plan 🦦

A multi-phase plan to take Otter from a functional-but-stock WinForms tray utility to a
polished, modern desktop app — using the colocated **claude-watch** project as the north star
for look, feel, and structure.

> Status: **Decisions locked** (2026-06-24) — see [§5 Decisions](#5-decisions-locked). The phases
> below reflect those answers.

---

## 1. Where we are vs. where we're going

**Otter today** (`src/`, net8.0-windows):

- `Program.cs` — single-instance mutex + `Application.Run`.
- `TrayApp.cs` — `NotifyIcon` + `ContextMenuStrip`; generated 16×16 dot icon; balloon tips.
- `AudioMonitor.cs` — polls Teams capture sessions every 5s, raises `CallStarted`/`CallEnded`.
- `SlackClient.cs` — OAuth (PKCE, no secret), get/set/clear status with restore-on-end logic.
- `Config.cs` — JSON in `%AppData%\Otter\config.json`.
- `SettingsWindow.cs` — a `FixedDialog` with stock GroupBoxes, light theme, default fonts.

**What makes claude-watch feel polished** (the patterns we'll borrow):

- A single shared **`Theme`** palette (dark) used by every surface.
- **Custom-painted controls** — `ToggleSwitch`, `Spinner`, progress bars — instead of stock checkboxes/buttons.
- A **settings window with a left nav rail + content pages** (Getting started, feature pages, About, Changelog), fluid-width and DPI-aware.
- **Dark title bar + dark scrollbars** via `NativeMethods` (DWM + uxtheme).
- A real **app icon** (`icon.ico`/`png`), embedded; centralized **`AppInfo`** (version, repo, issues).
- In-app **Changelog** rendered from an embedded `CHANGELOG.md`.
- **Velopack auto-update** + a GitHub Actions release workflow.

The gap is almost entirely *presentation and packaging* — Otter's core logic is sound. The revamp
is mostly additive: a design-system layer, a rebuilt settings window, a richer tray, and distribution.

---

## 2. Design principles (adopted from claude-watch)

1. **One theme, everywhere.** All colors/fonts come from a `Theme` class — no ad-hoc `Color.*`.
2. **Custom controls over stock.** Toggles, buttons, and status indicators are painted to match.
3. **Settings is first-class.** A proper window with navigation, not a cramped dialog.
4. **DPI-correct & fluid.** Auto-size to font; reflow to width; test at 125%/150%.
5. **Quiet by default, informative on demand.** The tray tells the story at a glance; detail lives in settings.
6. **Self-explaining.** Getting-started copy, an About page, and a Changelog ship in-app.

---

## 3. Phased plan

### Phase 0 — Foundations & branding  ✅ *done 2026-06-24*
*Goal: the scaffolding everything else hangs on.*

> Delivered: target framework bumped **net8 → net10**; `Otter.csproj` now carries `Version` (0.1.0),
> `Product`/`AssemblyTitle`, `ApplicationIcon`, and embeds `icon.ico`, `icon.png`, and `CHANGELOG.md`
> (logical name `Otter.CHANGELOG.md`). Added a real **otter app icon** — a white 🦦 glyph on an
> accent-gradient squircle — emitted as `icon.png` (256) + a multi-size `icon.ico`, with the
> generator committed at `tools/icongen/` so the assets are reproducible (verified byte-identical).
> The settings window loads the multi-size `.ico` for a crisp title-bar/taskbar icon, and the otter
> now shows in the banner + About. `AppInfo.cs` already landed in Phase 2.
> **Tray note:** the tray icon stays the colour-coded state dot (clearer than line-art at 16px); a
> future option is an otter-with-state-badge tray icon.

- **Bump target framework `net8.0-windows` → `net10.0-windows`** (decision #2). The net10 SDK
  (10.0.301) is confirmed installed locally, so this is low-risk and lets us reuse claude-watch
  code verbatim.
- Update `Otter.csproj`: `Version`, `Product`/`AssemblyTitle`, `ApplicationIcon`, embedded `icon.ico`/`icon.png`.
- Add **`AppInfo.cs`** (version, repo URL, issues URL) — single source of truth.
- Create an **otter app icon** (ico + png + source) and a tray-friendly variant — Otter currently
  ships **no icon asset**, so this is net-new.
- Add `CHANGELOG.md` and embed it.

### Phase 1 — Design system  ✅ *done 2026-06-24*
*Goal: a reusable **dark** UI toolkit (decision #1: dark-only), ported and slimmed for Otter's needs.*

> Delivered: `Theme.cs` (dark palette + `OtterState` status colours + `Blend`), dark-mode helpers
> added to `NativeMethods.cs` (`UseDarkTitleBar`, `UseDarkScrollBars`, `DestroyIcon`), and
> `SettingsControls.cs` (`ToggleSwitch`, `Spinner`, a reusable `FluidLayout`, and the `Ui` control
> factory). Builds clean on the existing net8 target; not yet wired into a window (that's Phase 2).

> **Dark-only** keeps things cohesive with claude-watch and avoids making every control
> theme-aware. The `Theme` class stays the single source of color so a light/system mode could
> still be layered on later without rework — but it is explicitly **out of scope** for now.

- Add **`Theme.cs`** — port claude-watch's palette (FormBg, Fg, Title, Muted, Accent, Border, Button*, Danger, status colors).
- Add **`NativeMethods`** dark helpers — `UseDarkTitleBar`, `UseDarkScrollBars`, `DestroyIcon` (Otter already has a `NativeMethods.cs` for audio; extend it).
- Add **custom controls** (`SettingsControls.cs`): `ToggleSwitch`, and the factory helpers Otter needs (`SectionTitle`, `BodyText`, `TitleRow`, `ButtonRow`, `MakeButton`, `MakeToggle`, `MakeTextBox`, `FieldCaption`, `Separator`, `LinkRow`). Port `Spinner` if async flows (OAuth) want it.

### Phase 2 — Settings window revamp  ✅ *done 2026-06-24*
*Goal: replace `SettingsWindow` with a claude-watch-style nav-rail window.*

> Delivered: `SettingsWindow` rebuilt as a dark, resizable nav-rail shell (rail + fluid content +
> Save/Cancel footer) using the Phase 1 `Ui`/`FluidLayout` toolkit, dark title bar + scrollbars.
> Pages shipped: **Getting started**, **Slack** (with a `Spinner` during OAuth), **Status** (with a
> live preview chip), and **About**. The `TrayApp` contract (`new SettingsWindow(config)` →
> `ShowDialog()` → `Result`) is unchanged, so Save/Cancel still cleanly commits or discards.
> Verified by screenshotting all four pages via a throwaway harness. `AppInfo.cs` was pulled forward
> from Phase 0 (About needs it). **Deferred:** Automation (run-at-login → Phase 4) and Changelog
> (needs an embedded `CHANGELOG.md` → Phase 0/5) — left out so every shipped page does something real.

Proposed pages:
- **Getting started** — banner (otter logo + tagline), what-it-does bullets, current connection state.
- **Slack** — client ID, Connect/Reconnect/Disconnect with a `Spinner` during OAuth, connection status line.
- **Status** — status text + emoji, with a live preview chip; sensible defaults.
- **Automation** — run-at-login toggle; (future) per-signal toggles.
- **About** — icon, version, GitHub links, Check-for-Updates button.
- **Changelog** — rendered from embedded `CHANGELOG.md`.

Mechanics to port: fluid-width reflow, dark title bar on `OnHandleCreated`, dark scrollbars on `OnShown`.

### Phase 3 — Tray experience  ✅ *done 2026-06-24*
*Goal: the tray reads as intentional, not default.*

> Delivered: a **dark-themed context menu** (`TrayMenu.cs` — `DarkMenuColorTable` +
> `DarkMenuRenderer`) matching the app; the menu now has a bold status **header with a colour-coded
> state dot** (the renderer skips its hover highlight) and accent check marks. Tray icons are now
> driven by `OtterState`/`Theme.StatusColor`, and icon swapping frees the old GDI handle via
> `NativeMethods.DestroyIcon` (fixing a per-refresh handle leak). Added a **"Show notifications"**
> quiet preference (`Config.NotificationsEnabled`) that gates the call-detected balloon, toggled from
> the tray. Verified by screenshotting the rendered menu. **Note:** the still-pending app-icon art
> (Phase 0) is what would let the tray dot become a true otter mark; the themed dot is the interim.

- Redesign the tray **status icon** set (monitoring / in-call / snoozed / disabled) to match the theme — crisper than the current ad-hoc dot, ideally derived from the otter mark.
- Richer **context menu**: current-state header, quick toggles, snooze submenu (keep existing 30/60/120 + a "clear"), Settings, Quit. Consider a themed popover (claude-watch has `PopoverMenu`) vs. staying with `ContextMenuStrip` — see decisions.
- Replace raw balloon tips with consistent, branded notifications; respect a "quiet" preference.

### Phase 4 — Feature & architecture  ✅ *done 2026-06-24*
*Goal: deliver on the README's promise ("first signal… across all your tools") — decision #3: refactor to pluggable signals.*

> Delivered: the pluggable-signal seam — `IStatusSignal` (named provider with `IsActive` + `Changed`),
> `SignalCoordinator` (aggregates providers, resolves precedence by list order, raises `ActiveChanged`
> only when the winner changes), and `TeamsCallSignal` (the old `AudioMonitor` logic, now the one live
> provider). `TrayApp` subscribes to the coordinator, not the monitor, and a new idempotent
> `ReevaluateStatus()` centralises set/clear so enable-toggle, snooze, and snooze-expiry all re-sync
> the Slack status correctly. `OtterState.InCall` generalised to `Active` (label comes from the active
> signal). Added **run-at-login** (`Startup.cs`, per-user Run key) surfaced on a new **Automation**
> settings page. Verified: clean build, `SignalCoordinator` unit-tested (precedence + events), and the
> Automation page rendered. **Not runtime-tested** against a live Teams call (the polling logic is
> unchanged from the prior `AudioMonitor`). Snooze "show remaining time" is covered by the existing
> header ("Snoozed until …"); a dedicated one-click *extend* was left out as minor.

- Refactor `AudioMonitor` into a small **signal-provider abstraction**. Sketch:
  - An `IStatusSignal` interface — a named provider that raises `Activated`/`Deactivated` and
    reports `IsActive` (Teams-call detection becomes `TeamsCallSignal : IStatusSignal`).
  - A `SignalCoordinator` that aggregates providers, resolves precedence when several are active,
    and drives the single Slack status update — so `TrayApp` subscribes to *it*, not to each monitor.
  - Per-signal config (which status text/emoji a signal maps to) lives in `Config`, surfaced on the
    Automation/Status pages.
- This keeps the Teams detector working exactly as today while making screen-lock, focused-app, and
  calendar signals additive later (no `TrayApp` churn). Ship Phase 4 with **Teams-only** still the
  one live provider — the value here is the seam, not new detectors yet.
- **Run at login** (registry `Run` key or startup shortcut), surfaced in Automation.
- Tighten snooze UX (show remaining time, one-click extend) and status restore edge cases.

### Phase 5 — Distribution & updates
*Goal: shippable like claude-watch — decision #4: full Velopack + GitHub release pipeline.*

- Add the **Velopack** package + bootstrap in `Program.Main` (the `VelopackApp.Build().Run()` entry hook).
- About → **Check for Updates** flow with a `Spinner` and clear up-to-date / downloading / restart states.
- Add a **GitHub Actions release workflow** triggered on `v*` tags — claude-watch's
  `.github/workflows/release.yml` is directly reusable (swap project name/exe to `Otter`): it
  publishes a self-contained single-file win-x64 build, then `vpk pack`s it.
- Add a `publish.bat`/script mirroring claude-watch's.
- Requires a **public GitHub repo** for Otter to host releases (Otter is currently local-only) —
  flagged as a prerequisite, not a blocker for Phases 0–4.
- Wire the in-app Changelog to releases.

### Phase 6 — Polish & QA
*Goal: the last 10% that makes it feel finished.*

- DPI passes at 100/125/150%; verify reflow and no clipped text.
- Error/empty states (Slack errors, no token, offline) styled consistently.
- Accessibility: tab order, keyboard activation of custom controls, contrast.
- README refresh with screenshots.

---

## 4. Suggested sequencing

Phases 0→1→2 are the backbone and unlock the biggest perceived-quality jump. 3 builds on the
design system. 4 and 5 are independent and can be reordered by priority. 6 is continuous.

---

## 5. Decisions (locked)
*Answered 2026-06-24.*

| # | Decision | Choice | Impact on the plan |
|---|----------|--------|--------------------|
| 1 | **Theme modes** | **Dark-only** | Phase 1 builds one dark `Theme`; no light/system path. `Theme` stays the single color source so a future mode is possible but out of scope. |
| 2 | **Target framework** | **Bump to net10** | Phase 0 retargets `net8.0-windows` → `net10.0-windows`. net10 SDK (10.0.301) confirmed installed; claude-watch code is reusable verbatim. |
| 3 | **Future signals** | **Refactor to pluggable signals** | Phase 4 introduces `IStatusSignal` + a `SignalCoordinator`; Teams remains the only live provider, but the seam is built now. |
| 4 | **Distribution & auto-update** | **Yes — full pipeline** | Phase 5 adds Velopack, an in-app updater, and a GitHub Actions release workflow (reusing claude-watch's). **Prerequisite:** a public GitHub repo to host releases. |

### Notes / follow-ups surfaced while planning
- **No app icon exists yet** — creating the otter mark (ico/png/source) is net-new work in Phase 0
  and gates the tray redesign (Phase 3) and banner (Phase 2).
- **Repo is local-only** — Phase 5's release pipeline needs a published GitHub repo; everything in
  Phases 0–4 works without it.
