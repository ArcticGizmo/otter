# Plan: Slack Status & Detection settings tabs + configurable matchers

## Goal

Now that call detection is app-agnostic (mic CapabilityAccessManager, not a Teams-specific
WASAPI probe), retire the "Integrations → Microsoft Teams" split and replace it with two
purpose-built settings tabs:

1. **Slack Status** — set the single status text + emoji, with live preview.
2. **Detection** — manage a list of products to detect (name + comma-separated match terms +
   enable toggle), add custom matchers without a rebuild, and a "track mic usage" live log to
   discover apps that aren't matching.

---

## Decisions (confirmed with user)

| # | Question | Decision |
|---|----------|----------|
| 1 | Match-text semantics | **Contains, on app identifier.** Each comma-separated term is a case-insensitive substring tested against the app's **exe filename** (NonPackaged) or **package family name** (packaged) — *not* the full encoded registry path. Flexible and matches the "contains check" the user described. Accepted trade-off: `teams` still matches `TeamSpeak.exe`; the Detection list + per-entry disable + the discovery log are the mitigation. |
| 2 | Status model | **Single global status.** One status text/emoji applied whenever *any* enabled product is detected, cleared when none are. Drops per-product status and per-signal precedence — aligns with the agnostic approach and the single "Slack Status" tab. |
| 3 | Quick-add behaviour | **Create a new enabled product**, named after the app, with the app's identifier as its match term. User can rename/edit afterwards. |
| 4 | Live-log behaviour | **Distinct apps, in-memory, while toggle on.** Last 20 *distinct* apps seen capturing the mic, most-recent-first, held in memory only, populated while "track mic usage" is on, reset on app restart. |

---

## Data model — `Config.cs`

Add a product list and the tracking toggle:

```csharp
class DetectionProduct
{
    public string Name    { get; set; } = "";   // friendly label, e.g. "Microsoft Teams"
    public string Match   { get; set; } = "";   // comma-separated terms, e.g. "teams"
    public bool   Enabled { get; set; } = true;

    // Helper (not serialized): split Match on ',', trim, drop empties.
    [JsonIgnore] public IEnumerable<string> Terms =>
        Match.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0);
}
```

In `Config`:

```csharp
public List<DetectionProduct> DetectionProducts { get; set; } = DefaultProducts();
public bool TrackMicUsage { get; set; } = false;

static List<DetectionProduct> DefaultProducts() => new()
{
    new() { Name = "Microsoft Teams", Match = "teams",   Enabled = true },
    new() { Name = "Zoom",            Match = "zoom",    Enabled = true },
    new() { Name = "Discord",         Match = "discord", Enabled = true },
};
```

**Migration:** the auto-initializer seeds the three defaults. Existing `config.json` files predate
the key, so deserialization leaves the seeded defaults in place (they get written on next save). A
user who deletes all products persists an empty list, which is respected (no re-seed). `StatusText`
/ `StatusEmoji` already exist and are reused unchanged by the Slack Status tab.

> Note: `"teams"` covers all Teams variants — packaged PFN `MSTeams_…` (contains "teams"), classic
> `Teams.exe`, and new `ms-teams.exe`. `"discord"` → `Discord.exe`, `"zoom"` → `Zoom.exe`.

---

## Signal / scanner — `MicrophoneInUseSignal.cs`

The class stops being one-app-per-instance and becomes a single mic monitor driven by the config's
matchers, also exposing the discovery feed.

### Responsibilities
- Poll the ConsentStore every 5s (unchanged cadence).
- **Enumerate every currently-capturing app** (not just short-circuit on first match), each yielding
  an *identifier*:
  - **NonPackaged** child key: encoded path; identifier = last `#`-segment = exe filename (e.g.
    `Zoom.exe`).
  - **Packaged** key: identifier = the PackageFamilyName (e.g. `MSTeams_8wekyb3d8bbwe`).
  - Capturing test (`LastUsedTimeStop == 0 && LastUsedTimeStart > 0`) still checks the app key and
    its descendants.
- `IsActive` = any capturing identifier contains any term from any **enabled** product
  (case-insensitive).
- Maintain the rolling discovery log when tracking is on.

### New surface
```csharp
record MicCapture(string Identifier);   // exe filename or PFN; match state computed at display time

interface IMicUsageFeed
{
    bool TrackingEnabled { get; set; }
    IReadOnlyList<MicCapture> RecentCaptures { get; }   // distinct, most-recent-first, max 20
    event Action? CapturesChanged;
}
```
- `MicrophoneInUseSignal : IStatusSignal, IMicUsageFeed`.
- `void UpdateMatchers(IEnumerable<DetectionProduct> products)` — swaps the enabled-term set
  atomically (called when Detection config changes; avoids rebuilding the coordinator).
- Rolling-log update: each poll, any app that is capturing now but wasn't last poll (a new capture
  start) is promoted to the front of the distinct list (dedupe by identifier, case-insensitive),
  capped at 20. Only mutated while `TrackingEnabled`. Fires `CapturesChanged`.
- **Thread-safety:** poll runs on a background thread. Guard the log with a lock; expose
  `RecentCaptures` as a snapshot copy. UI marshals `CapturesChanged` via `BeginInvoke` (same pattern
  as `EmojiStore.Updated` in `SettingsWindow`).

### Removed
- The `Teams()` / `Zoom()` static factories and the per-instance `_exeNames`/`_packagePrefixes`
  ctor (superseded by config-driven matchers). Match logic moves to "contains on identifier".

### `SignalCoordinator` / `IStatusSignal`
Kept as-is (good seam for future non-mic signals). The coordinator now holds the single
`MicrophoneInUseSignal`. Precedence is moot with one signal + global status.

---

## Settings UI — `SettingsWindow.cs` / `SettingsControls.cs`

### Nav restructure (`BuildLayout`)
Remove `integrations` and the nested `teams` pages. New order:

```
start  | Getting started
status | Slack Status      (new)
detect | Detection         (new)
snooze | Snooze
about  | About
changelog | Changelog
```

### Slack Status page (`BuildStatusPage`)
Move the existing status UI out of `BuildTeamsPage` verbatim: status-text box, emoji box +
`EmojiAutocomplete`, separator, preview row (`_previewEmoji` + `_statusPreview`). `UpdateStatusPreview`
and `CommitPendingEdits` keep working against the same `_statusTextBox`/`_emojiBox` fields.

### Detection page (`BuildDetectionPage`)
1. **Product list** — a rebuildable container panel; one row per `DetectionProduct`:
   - enable `ToggleSwitch` → sets `Enabled`, commits.
   - `Name` text box (commit on leave).
   - `Match` (CSV) text box (commit on leave).
   - "Remove" button → drops the entry, rebuilds list, commits.
   - "Add product" button → appends a blank enabled entry and focuses its name.
   - Likely a small `Ui` helper (e.g. `Ui.ProductRow(...)`) for the row layout, reusing
     `MakeTextBox`/`MakeToggle`/`MakeButton`.
2. **Track mic usage** — a `TitleRow` toggle bound to `_config.TrackMicUsage`; on change, persist and
   set `feed.TrackingEnabled`.
3. **Live log** — below the toggle, a list rebuilt from `feed.RecentCaptures`:
   - Row shows the app identifier.
   - **Matched** (identifier contains an enabled term) → rendered in `Theme.Green`, no button.
   - **Not matched** → a **Quick add** button that creates a new enabled product
     (`Name` = identifier without extension, `Match` = identifier), commits, refreshes both the
     product list and the log (the row then turns green).
   - Subscribe to `feed.CapturesChanged` while the page is shown; unsubscribe on close/dispose.
     Refresh via `BeginInvoke`. Show an empty-state hint when the log is empty / tracking is off.

### Constructor change
`SettingsWindow` gains an `IMicUsageFeed feed` parameter so it can read `RecentCaptures`, toggle
tracking, and subscribe to updates. Match-state for log rows is computed in the window from the
current `_config.DetectionProducts` (so it updates the instant a matcher is added).

### Copy cleanups (cosmetic)
- Getting started "What it does" bullet and the banner tagline ("…starting with Teams calls") →
  generalise to "calls" rather than Teams-specific.
- Class XML-doc on `SettingsWindow` mentioning the Integrations/Teams pages.

---

## TrayApp wiring — `TrayApp.cs`

- Build the signal from config instead of a factory:
  `var mic = new MicrophoneInUseSignal(); mic.UpdateMatchers(_config.DetectionProducts); mic.TrackingEnabled = _config.TrackMicUsage;`
  then register it with the coordinator. Hold a typed reference (`IMicUsageFeed`) to pass to settings.
- `OnOpenSettings`: pass the feed into `new SettingsWindow(_config, …, feed)`.
- `OnSettingsChanged`: after persisting, call `mic.UpdateMatchers(_config.DetectionProducts)` (and
  sync `TrackingEnabled`) before `ReevaluateStatus()` / `RefreshUI()`, so matcher edits take effect
  live.

---

## Edge cases & risks

- **False positives** (e.g. `teams` ⇒ `TeamSpeak.exe`): accepted per decision #1; mitigated by the
  disable toggle and the discovery log. Document near the Match field.
- **Browser calls** (Google Meet, Teams/Zoom web): captured under `chrome.exe`/`msedge.exe`; can't be
  attributed to a call, so matching a browser would be a broad false positive. Out of scope; mention
  in UI help text.
- **Stale "in use"** record if an app crashes mid-capture (`LastUsedTimeStop` never written): existing
  caveat — can leave status stuck / a stale green row until the app next cleanly starts+stops.
- **Latency:** 5s poll means the live log and status lag reality by up to ~5s. Acceptable; note it.
- **Thread-safety:** background poll vs UI reads of the feed — lock + snapshot + `BeginInvoke`.
- **All products removed / all disabled:** no detection fires; status never set. Intended.

## Out of scope (call out, don't build)
- Per-product status text/emoji (explicitly dropped).
- Persisting the live log across restarts (decision #4 = in-memory only).
- Non-mic signals (screen-lock, calendar) — the `IStatusSignal` seam remains for later.
- A CHANGELOG entry / version bump — do via `/bump-version` when the feature lands.

---

## Implementation checklist (suggested order)
1. `Config`: add `DetectionProduct`, `DetectionProducts` (seeded), `TrackMicUsage`; verify
   migration on an existing `config.json`.
2. `MicrophoneInUseSignal`: enumerate-all scan, config-driven contains matching, `UpdateMatchers`,
   `IMicUsageFeed` + rolling distinct log (locked), remove factories.
3. `TrayApp`: build signal from config, hold feed, wire `UpdateMatchers`/`TrackingEnabled` on change,
   pass feed to settings.
4. `SettingsWindow`: nav restructure; Slack Status page (move status UI); Detection page (product
   list CRUD + tracking toggle + live log with quick-add); constructor takes feed; copy cleanups.
5. `SettingsControls`: any shared row helper (`ProductRow`) + reuse existing factories.
6. Manual verification (after user OK to run): defaults seed; add/remove/disable products; status
   sets/clears on a real call; tracking log lists capturing apps, greens matches, quick-add works.

## Open questions
None blocking — all four design decisions are resolved above. Re-confirm only if the contains
false-positive behaviour proves noisy in practice (could later offer an exact-match opt-in per entry).
