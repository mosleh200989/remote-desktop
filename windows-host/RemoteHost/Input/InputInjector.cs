using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RemoteHost.Input;

/// <summary>
/// Synthesizes mouse and keyboard input via the standard Win32 SendInput API
/// (the same API used by every legitimate remote-input / accessibility tool -
/// this does not touch UAC, the lock screen, or any protected surface, and
/// Windows will simply refuse to deliver input to a secure desktop such as
/// the UAC consent prompt or the Ctrl+Alt+Del screen).
/// </summary>
public sealed class InputInjector
{
    /// <summary>The bounds (in virtual-screen coordinates) of the monitor currently being captured.</summary>
    public Rect TargetMonitorBounds { get; set; }

    public void MoveMouseNormalized(double nx, double ny)
    {
        var b = TargetMonitorBounds;
        double targetX = b.Left + nx * (b.Right - b.Left);
        double targetY = b.Top + ny * (b.Bottom - b.Top);
        SendAbsoluteMouse(targetX, targetY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0, 0);
    }

    public void MouseButton(bool down, int button, double nx, double ny)
    {
        MoveMouseNormalized(nx, ny);
        uint flags = button switch
        {
            0 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            1 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            2 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            _ => 0,
        };
        if (flags == 0) return;
        var b = TargetMonitorBounds;
        double targetX = b.Left + nx * (b.Right - b.Left);
        double targetY = b.Top + ny * (b.Bottom - b.Top);
        SendAbsoluteMouse(targetX, targetY, flags | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0, 0);
    }

    public void MouseWheel(double dx, double dy, double nx, double ny)
    {
        MoveMouseNormalized(nx, ny);
        // WHEEL_DELTA = 120 per notch; browser deltaY is in pixels, scale down.
        if (dy != 0)
            SendAbsoluteMouse(0, 0, MOUSEEVENTF_WHEEL, (int)Math.Clamp(-dy * 4, -1200, 1200), 0);
        if (dx != 0)
            SendAbsoluteMouse(0, 0, MOUSEEVENTF_HWHEEL, (int)Math.Clamp(dx * 4, -1200, 1200), 0);
    }

    public void KeyEvent(bool down, string key)
    {
        if (TryMapNamedKey(key, out var vk))
        {
            SendKeyboard(vk, down, unicode: false);
            return;
        }
        if (key.Length == 1)
        {
            SendUnicodeChar(key[0], down);
        }
    }

    public void TypeText(string text)
    {
        foreach (var ch in text)
        {
            SendUnicodeChar(ch, true);
            SendUnicodeChar(ch, false);
        }
    }

    private static bool TryMapNamedKey(string key, out ushort vk)
    {
        vk = key switch
        {
            "Enter" => 0x0D,
            "Backspace" => 0x08,
            "Tab" => 0x09,
            "Escape" => 0x1B,
            "Delete" => 0x2E,
            "ArrowUp" => 0x26,
            "ArrowDown" => 0x28,
            "ArrowLeft" => 0x25,
            "ArrowRight" => 0x27,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "Control" => 0x11,
            "Shift" => 0x10,
            "Alt" => 0x12,
            "Meta" or "OS" => 0x5B, // left Windows key
            "CapsLock" => 0x14,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            _ => 0,
        };
        return vk != 0;
    }

    // ---- Win32 plumbing ----

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x01000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private static void SendAbsoluteMouse(double screenX, double screenY, uint flags, int mouseData, int _unused)
    {
        int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vWidth = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int vHeight = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        int normX = (int)Math.Clamp((screenX - vLeft) * 65535.0 / vWidth, 0, 65535);
        int normY = (int)Math.Clamp((screenY - vTop) * 65535.0 / vHeight, 0, 65535);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (flags & MOUSEEVENTF_ABSOLUTE) != 0 ? normX : 0,
                    dy = (flags & MOUSEEVENTF_ABSOLUTE) != 0 ? normY : 0,
                    mouseData = unchecked((uint)mouseData),
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyboard(ushort vk, bool down, bool unicode)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = unicode ? (ushort)0 : vk,
                    wScan = unicode ? vk : (ushort)0,
                    dwFlags = (down ? 0u : KEYEVENTF_KEYUP) | (unicode ? KEYEVENTF_UNICODE : 0u),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendUnicodeChar(char ch, bool down) => SendKeyboard(ch, down, unicode: true);
}

public readonly struct Rect
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; }
    public double Bottom { get; init; }
}
