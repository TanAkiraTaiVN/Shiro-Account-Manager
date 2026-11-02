#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Shiro.Services
{
    public static partial class RobloxAudioService
    {
        private const uint COINIT_MULTITHREADED = 0x0;
        private const uint CLSCTX_ALL = 0x17;
        private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

        private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
        private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        private static readonly Guid IID_IAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C32");
        private static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [LibraryImport("ole32.dll")]
        private static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [LibraryImport("ole32.dll")]
        private static partial void CoUninitialize();

        [LibraryImport("ole32.dll")]
        private static unsafe partial int CoCreateInstance(
            Guid* rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            Guid* riid,
            IntPtr* ppv);

        /// <summary>
        /// Direct unmanaged vtable wrapper for ISimpleAudioVolume ({87CE5498-68D6-44E5-9215-6DA47EF883D8}).
        /// Bypasses CLR RCW apartment marshaling completely so WASAPI works reliably on both STA and MTA threads.
        /// </summary>
        internal readonly struct NativeSimpleAudioVolume
        {
            private readonly IntPtr _ptr;

            public NativeSimpleAudioVolume(IntPtr ptr) => _ptr = ptr;

            public unsafe bool TryGetMute(out bool isMuted)
            {
                isMuted = false;
                if (_ptr == IntPtr.Zero) return false;

                void** vtable = *(void***)_ptr;
                var fnGetMute = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtable[6];
                int rawMute = 0;
                int hr = fnGetMute(_ptr, &rawMute);
                if (hr < 0) return false;

                isMuted = rawMute != 0;
                return true;
            }

            public unsafe bool TrySetMute(bool mute)
            {
                if (_ptr == IntPtr.Zero) return false;

                void** vtable = *(void***)_ptr;
                var fnSetMute = (delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, int>)vtable[5];
                Guid eventCtx = Guid.Empty;
                int hr = fnSetMute(_ptr, mute ? 1 : 0, &eventCtx);
                return hr >= 0;
            }
        }

        private static unsafe int ComQueryInterface(IntPtr pUnk, in Guid iid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            if (pUnk == IntPtr.Zero) return -1;

            Guid localIid = iid;
            IntPtr result = IntPtr.Zero;
            void** vtable = *(void***)pUnk;
            var fnQi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[0];
            int hr = fnQi(pUnk, &localIid, &result);
            if (hr >= 0)
            {
                ppv = result;
            }
            return hr;
        }

        private static unsafe void ComRelease(ref IntPtr pUnk)
        {
            IntPtr ptr = pUnk;
            if (ptr == IntPtr.Zero) return;
            pUnk = IntPtr.Zero;

            void** vtable = *(void***)ptr;
            var fnRelease = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
            fnRelease(ptr);
        }

        private static unsafe int DeviceEnumeratorGetDefaultEndpoint(IntPtr pEnumerator, int dataFlow, int role, out IntPtr ppEndpoint)
        {
            ppEndpoint = IntPtr.Zero;
            IntPtr result = IntPtr.Zero;
            void** vtable = *(void***)pEnumerator;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)vtable[4];
            int hr = fn(pEnumerator, dataFlow, role, &result);
            if (hr >= 0) ppEndpoint = result;
            return hr;
        }

        private static unsafe int DeviceActivateSessionManager2(IntPtr pDevice, out IntPtr ppSessionManager2)
        {
            ppSessionManager2 = IntPtr.Zero;
            Guid iid = IID_IAudioSessionManager2;
            IntPtr result = IntPtr.Zero;
            void** vtable = *(void***)pDevice;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)vtable[3];
            int hr = fn(pDevice, &iid, CLSCTX_ALL, IntPtr.Zero, &result);
            if (hr >= 0) ppSessionManager2 = result;
            return hr;
        }

        private static unsafe int SessionManager2GetEnumerator(IntPtr pSessionManager2, out IntPtr ppSessionEnum)
        {
            ppSessionEnum = IntPtr.Zero;
            IntPtr result = IntPtr.Zero;
            void** vtable = *(void***)pSessionManager2;
            // IAudioSessionManager2 inherits GetAudioSessionControl [3] and GetSimpleAudioVolume [4] from IAudioSessionManager
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtable[5];
            int hr = fn(pSessionManager2, &result);
            if (hr >= 0) ppSessionEnum = result;
            return hr;
        }

        private static unsafe int SessionEnumeratorGetCount(IntPtr pSessionEnum, out int count)
        {
            int c = 0;
            void** vtable = *(void***)pSessionEnum;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtable[3];
            int hr = fn(pSessionEnum, &c);
            count = hr >= 0 ? c : 0;
            return hr;
        }

        private static unsafe int SessionEnumeratorGetSession(IntPtr pSessionEnum, int index, out IntPtr ppSession)
        {
            ppSession = IntPtr.Zero;
            IntPtr result = IntPtr.Zero;
            void** vtable = *(void***)pSessionEnum;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)vtable[4];
            int hr = fn(pSessionEnum, index, &result);
            if (hr >= 0) ppSession = result;
            return hr;
        }

        private static unsafe int SessionControl2GetProcessId(IntPtr pSessionControl2, out uint pid)
        {
            uint p = 0;
            void** vtable = *(void***)pSessionControl2;
            // IAudioSessionControl has 9 methods [3..11]; IAudioSessionControl2::GetProcessId is at slot [14]
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)vtable[14];
            int hr = fn(pSessionControl2, &p);
            pid = hr >= 0 ? p : 0;
            return hr;
        }
    }
}
