using System.Runtime.InteropServices;

namespace Otter;

/// <summary>
/// Queries Windows Audio Session API (WASAPI) for processes with active
/// microphone capture sessions. Used to detect Teams calls.
/// </summary>
static class NativeMethods
{
    public static List<string> GetActiveCaptureSessions()
    {
        var result = new List<string>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessionEnum = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out device);
            if (device == null) return result;

            device.Activate(typeof(IAudioSessionManager2).GUID, 0, IntPtr.Zero, out var managerObj);
            manager = (IAudioSessionManager2)managerObj;
            manager.GetSessionEnumerator(out sessionEnum);
            sessionEnum.GetCount(out var count);

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? session = null;
                try
                {
                    sessionEnum.GetSession(i, out session);
                    if (session is not IAudioSessionControl2 session2) continue;

                    session.GetState(out var state);
                    if (state != AudioSessionState.Active) continue;

                    session2.GetProcessId(out var pid);
                    if (pid == 0) continue;

                    try
                    {
                        var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                        result.Add(proc.ProcessName);
                    }
                    catch { /* process may have exited */ }
                }
                finally { if (session != null) Marshal.ReleaseComObject(session); }
            }
        }
        catch { /* audio device temporarily unavailable */ }
        finally
        {
            if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
            if (manager     != null) Marshal.ReleaseComObject(manager);
            if (device      != null) Marshal.ReleaseComObject(device);
            if (enumerator  != null) Marshal.ReleaseComObject(enumerator);
        }

        return result;
    }

    // ── Enums ─────────────────────────────────────────────────────────────────

    enum EDataFlow { eRender, eCapture, eAll }
    enum ERole     { eConsole, eMultimedia, eCommunications }

    public enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    // ── COM co-class ──────────────────────────────────────────────────────────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCoClass { }

    // ── COM interfaces ────────────────────────────────────────────────────────

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int clsCtx,
                      IntPtr activationParams,
                      [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int stgmAccess, out IntPtr store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr sessionGuid, int streamFlags,
                                    out IAudioSessionControl session);
        void GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr volume);
        void GetSessionEnumerator(out IAudioSessionEnumerator sessionList);
        void RegisterSessionNotification(IntPtr notify);
        void UnregisterSessionNotification(IntPtr notify);
        void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID,
                                      IntPtr notify);
        void UnregisterDuckNotification(IntPtr notify);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        void GetCount(out int sessionCount);
        void GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        void GetState(out AudioSessionState state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
        void GetGroupingParam(out Guid groupingParam);
        void SetGroupingParam(ref Guid overrideParam, IntPtr ctx);
        void RegisterAudioSessionNotification(IntPtr notify);
        void UnregisterAudioSessionNotification(IntPtr notify);
    }

    // Inherits IAudioSessionControl vtable then adds its own methods
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        void GetState(out AudioSessionState state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
        void GetGroupingParam(out Guid groupingParam);
        void SetGroupingParam(ref Guid overrideParam, IntPtr ctx);
        void RegisterAudioSessionNotification(IntPtr notify);
        void UnregisterAudioSessionNotification(IntPtr notify);
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        void GetProcessId(out uint retVal);
        void IsSystemSoundsSession();
        void SetDuckingPreference(bool optOut);
    }
}
