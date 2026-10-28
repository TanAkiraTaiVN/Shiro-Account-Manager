#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Shiro.Services
{
    public static partial class RobloxAudioService
    {
        // Win32 Interop
        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // Core Audio COM Interop
        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppEndpoints);

            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);

            [PreserveSig]
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

            [PreserveSig]
            int RegisterEndpointNotificationCallback(IntPtr pClient);

            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

            [PreserveSig]
            int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);

            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

            [PreserveSig]
            int GetState(out int pdwState);
        }

        [ComImport]
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig]
            int GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags, out IntPtr sessionControl);

            [PreserveSig]
            int GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags, out IntPtr audioVolume);

            [PreserveSig]
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

            [PreserveSig]
            int RegisterSessionNotification(IntPtr newNotifications);

            [PreserveSig]
            int UnregisterSessionNotification(IntPtr newNotifications);

            [PreserveSig]
            int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr newNotifications);

            [PreserveSig]
            int UnregisterDuckNotification(IntPtr newNotifications);
        }

        [ComImport]
        [Guid("E2F5EE11-070C-401E-B503-4B0709C48B08")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig]
            int GetCount(out int sessionCount);

            [PreserveSig]
            int GetSession(int sessionIndex, out IAudioSessionControl session);
        }

        [ComImport]
        [Guid("F4B012C2-7405-44DD-85CE-E42045A8AEB2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            [PreserveSig]
            int GetState(out int pRetVal);

            [PreserveSig]
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

            [PreserveSig]
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

            [PreserveSig]
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

            [PreserveSig]
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

            [PreserveSig]
            int GetGroupingParam(out Guid pRetVal);

            [PreserveSig]
            int SetGroupingParam(ref Guid @override, ref Guid eventContext);

            [PreserveSig]
            int RegisterAudioSessionNotification(IntPtr newNotifications);

            [PreserveSig]
            int UnregisterAudioSessionNotification(IntPtr newNotifications);
        }

        [ComImport]
        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C32")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // Vtable layout of base IAudioSessionControl
            [PreserveSig]
            int GetState(out int pRetVal);

            [PreserveSig]
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

            [PreserveSig]
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

            [PreserveSig]
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

            [PreserveSig]
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

            [PreserveSig]
            int GetGroupingParam(out Guid pRetVal);

            [PreserveSig]
            int SetGroupingParam(ref Guid @override, ref Guid eventContext);

            [PreserveSig]
            int RegisterAudioSessionNotification(IntPtr newNotifications);

            [PreserveSig]
            int UnregisterAudioSessionNotification(IntPtr newNotifications);

            // IAudioSessionControl2 methods
            [PreserveSig]
            int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

            [PreserveSig]
            int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

            [PreserveSig]
            int GetProcessId(out uint pRetVal);

            [PreserveSig]
            int IsSystemSoundsSession();

            [PreserveSig]
            int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [ComImport]
        [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig]
            int SetMasterVolume(float fLevel, ref Guid eventContext);

            [PreserveSig]
            int GetMasterVolume(out float pfLevel);

            [PreserveSig]
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid eventContext);

            [PreserveSig]
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }
    }
}
