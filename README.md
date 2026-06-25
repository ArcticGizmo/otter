<h1 align="center">Otter</h1>
<p align="center">
 <img src="./src/icon.png" width="150" />
</p>

## Never forget to update your Slack status again

You hop on a Teams call. Someone messages you on Slack expecting a quick reply, not realising you're heads-down in a meeting. You forget to set your status — every time.

Otter fixes that. It quietly watches for when you're on a Microsoft Teams call and sets your Slack status for you, then clears it the moment the call ends. No buttons to press, no habit to build. It just works in the background from your system tray.

<br>

## Why you'll like it

- **Completely hands-off** — your Slack status updates itself when a call starts and clears when it ends.
- **Make it yours** — pick the status text and emoji you want, with a live preview so you see exactly how it'll look in Slack.
- **Out of your way** — Otter lives in the system tray. Left-click the icon to open settings; right-click for a quick menu showing your current status.
- **Snooze when you need to** — heading into back-to-back calls you'd rather not broadcast? Pause Otter for a while from the tray.
- **Quiet if you want it** — keep the "call detected" notification on, or turn it off.
- **Starts with Windows** — flip one toggle and Otter is ready every time you log in.
- **Your data stays yours** — Otter connects to Slack with a secure sign-in and stores everything locally on your machine. It only watches _whether_ Teams is using your microphone, never what's said on the call.

## Getting started

1. Download and run Otter — it appears in your system tray.
2. Left-click the tray icon to open settings.
3. On **Getting started**, click **Connect** to sign in to your Slack workspace.
4. Set the status text and emoji you'd like under **Status** (or keep the defaults).
5. That's it. Next time you join a Teams call, your Slack status updates itself.

> **Note:** Otter is a Windows app and currently detects Microsoft Teams calls. Support for more apps and signals is on the way.

---

## Development

Otter is a .NET Windows tray application (C# / WinForms).

Run it locally:

```
dotnet run --project src\Otter.csproj
```

See [CHANGELOG.md](CHANGELOG.md) for release history.
