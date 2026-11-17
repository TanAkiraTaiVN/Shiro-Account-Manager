#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Shiro.Services
{
    public static partial class AntiAfkService
    {
        internal static nuint BuildKeyUpLParam(uint scanCode)
            => (nuint)(1u | (scanCode << 16) | (1u << 30) | (1u << 31));

        internal static ClientPulseOutcome EvaluateSilentFirstPulse(
            IntPtr hWnd,
            bool isHidden,
            bool isForegroundWindow,
            bool allowFocusSteal,
            DateTime nowUtc,
            int intervalMinutes,
            Func<IntPtr, bool> postPulse,
            int previousFailures)
            => ProcessClientPulseForTest(
                hWnd,
                isHidden,
                isForegroundWindow,
                allowFocusSteal,
                nowUtc,
                intervalMinutes,
                postPulse,
                postPulse,
                previousFailures);

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)]
            public uint type;
            [FieldOffset(8)]
            public MOUSEINPUT mi;
            [FieldOffset(8)]
            public KEYBDINPUT ki;
            [FieldOffset(8)]
            public HARDWAREINPUT hi;
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        private static unsafe partial uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

        private static unsafe void SendKey(ushort virtualKey, bool keyUp)
        {
            if (!OperatingSystem.IsWindows()) return;

            ushort scanCode = (ushort)MapVirtualKey(virtualKey, 0);
            INPUT input = default;
            input.type = INPUT_KEYBOARD;
            input.ki.wVk = virtualKey;
            input.ki.wScan = scanCode;
            input.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : KEYEVENTF_KEYDOWN;
            input.ki.time = 0;
            input.ki.dwExtraInfo = UIntPtr.Zero;

            SendInput(1, &input, sizeof(INPUT));
        }
    }
}
