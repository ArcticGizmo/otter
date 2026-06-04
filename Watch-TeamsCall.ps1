#Requires -Version 5.1
<#
.SYNOPSIS
    Monitors Microsoft Teams for active calls and syncs your Slack status.

.DESCRIPTION
    Polls Windows Audio Capture sessions every few seconds. When Teams is
    actively capturing audio (i.e. you are on a call), your Slack status is
    set to "In a Teams call". When the call ends the status is cleared.

.SETUP
    1. Create a Slack app at https://api.slack.com/apps
       - Add OAuth scope: users.profile:write
       - Install to workspace and copy the "User OAuth Token" (xoxp-...)
    2. Fill in the CONFIG section below.
    3. Run:  .\Watch-TeamsCall.ps1
       Or to start hidden in the background:
         Start-Process powershell -ArgumentList "-WindowStyle Hidden -File `"$PWD\Watch-TeamsCall.ps1`"" -WindowStyle Hidden

.NOTES
    To stop the background process:  Get-Process powershell | Where-Object MainWindowTitle -eq '' | Stop-Process
    Or just close the window if running interactively.
#>

# ── CONFIG ────────────────────────────────────────────────────────────────────

$SlackToken       = "xoxp-YOUR-TOKEN-HERE"   # Slack User OAuth Token
$StatusText       = "In a Teams call"
$StatusEmoji      = ":teams:"                 # or :telephone_receiver: :headphones:
$StatusExpireMins = 0                         # 0 = no expiry; set e.g. 120 for 2-hour auto-clear
$PollIntervalSec  = 5

# Teams process names to watch (both classic and new Teams are included)
$TeamsProcessNames = @("ms-teams")

# ── END CONFIG ────────────────────────────────────────────────────────────────

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Windows Audio Session detection (inline C#) ───────────────────────────────

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class AudioCapture
{
    // Returns the list of process names that currently have an active audio
    // capture (microphone) session on the default capture device.
    public static List<string> GetActiveCaptureSessions()
    {
        var result = new List<string>();
        IMMDeviceEnumerator enumerator = null;
        IMMDevice device = null;
        IAudioSessionManager2 manager = null;
        IAudioSessionEnumerator sessionEnum = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            // eCapture = 1, eConsole = 0
            enumerator.GetDefaultAudioEndpoint(1, 0, out device);
            if (device == null) return result;

            object managerObj;
            device.Activate(typeof(IAudioSessionManager2).GUID, 0, IntPtr.Zero, out managerObj);
            manager = (IAudioSessionManager2)managerObj;
            manager.GetSessionEnumerator(out sessionEnum);

            int count;
            sessionEnum.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl session = null;
                IAudioSessionControl2 session2 = null;
                try
                {
                    sessionEnum.GetSession(i, out session);
                    session2 = session as IAudioSessionControl2;
                    if (session2 == null) continue;

                    uint pid;
                    session2.GetProcessId(out pid);
                    if (pid == 0) continue;

                    // AudioSessionState: Inactive=0, Active=1, Expired=2
                    AudioSessionState state;
                    session.GetState(out state);
                    if (state != AudioSessionState.AudioSessionStateActive) continue;

                    try
                    {
                        var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                        result.Add(proc.ProcessName.ToLowerInvariant());
                    }
                    catch { /* process may have exited */ }
                }
                finally
                {
                    if (session2 != null) Marshal.ReleaseComObject(session2);
                    else if (session != null) Marshal.ReleaseComObject(session);
                }
            }
        }
        catch { /* device not available */ }
        finally
        {
            if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
            if (manager   != null) Marshal.ReleaseComObject(manager);
            if (device    != null) Marshal.ReleaseComObject(device);
            if (enumerator!= null) Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    // ── COM interfaces ────────────────────────────────────────────────────────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator {}

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int clsCtx,
                     IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int stgmAccess, out IntPtr store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IAudioSessionControl session);
        int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionList);
        int RegisterSessionNotification(IntPtr notify);
        int UnregisterSessionNotification(IntPtr notify);
        int RegisterDuckNotification(string sessionID, IntPtr notify);
        int UnregisterDuckNotification(IntPtr notify);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);
        int GetSession(int sessionCount, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        int GetState(out AudioSessionState state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
        int RegisterAudioSessionNotification(IntPtr notify);
        int UnregisterAudioSessionNotification(IntPtr notify);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // Inherits IAudioSessionControl — repeat the vtable slots
        int GetState(out AudioSessionState state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
        int RegisterAudioSessionNotification(IntPtr notify);
        int UnregisterAudioSessionNotification(IntPtr notify);
        // IAudioSessionControl2 additions
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int GetProcessId(out uint retVal);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    public enum AudioSessionState { AudioSessionStateInactive = 0, AudioSessionStateActive = 1, AudioSessionStateExpired = 2 }
}
'@ -Language CSharp

# ── Slack helpers ─────────────────────────────────────────────────────────────

function Set-SlackStatus {
    param([string]$Text, [string]$Emoji, [int]$ExpireMins)
    $expiry = if ($ExpireMins -gt 0) { [DateTimeOffset]::UtcNow.AddMinutes($ExpireMins).ToUnixTimeSeconds() } else { 0 }
    $body = @{
        profile = @{
            status_text       = $Text
            status_emoji      = $Emoji
            status_expiration = $expiry
        }
    } | ConvertTo-Json -Depth 3
    Invoke-RestMethod -Uri "https://slack.com/api/users.profile.set" `
        -Method Post `
        -Headers @{ Authorization = "Bearer $SlackToken" } `
        -ContentType "application/json; charset=utf-8" `
        -Body $body | Out-Null
}

function Clear-SlackStatus {
    Set-SlackStatus -Text "" -Emoji "" -ExpireMins 0
}

# ── Main loop ─────────────────────────────────────────────────────────────────

$wasInCall = $false
Write-Host "$(Get-Date -Format 'HH:mm:ss')  Watching for Teams calls... (Ctrl+C to stop)"

try {
    while ($true) {
        $activeSessions = [AudioCapture]::GetActiveCaptureSessions()
        $teamsNamesLower = $TeamsProcessNames | ForEach-Object { $_.ToLowerInvariant() }
        $inCall = ($activeSessions | Where-Object { $teamsNamesLower -contains $_ }).Count -gt 0

        if ($inCall -and -not $wasInCall) {
            Write-Host "$(Get-Date -Format 'HH:mm:ss')  Call started — setting Slack status"
            Set-SlackStatus -Text $StatusText -Emoji $StatusEmoji -ExpireMins $StatusExpireMins
            $wasInCall = $true
        }
        elseif (-not $inCall -and $wasInCall) {
            Write-Host "$(Get-Date -Format 'HH:mm:ss')  Call ended   — clearing Slack status"
            Clear-SlackStatus
            $wasInCall = $false
        }

        Start-Sleep -Seconds $PollIntervalSec
    }
}
finally {
    # Always clean up status if the script is stopped mid-call
    if ($wasInCall) {
        Write-Host "$(Get-Date -Format 'HH:mm:ss')  Script stopped mid-call — clearing Slack status"
        Clear-SlackStatus
    }
}
