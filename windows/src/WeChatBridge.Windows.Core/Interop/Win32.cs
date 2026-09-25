using System.Runtime.InteropServices;

namespace WeChatBridge.Windows.Core.Interop;

/// <summary>
/// 转发链路需要的全部 Win32 入口，刻意保持最小：前台窗口读写、窗口恢复、
/// 一次键盘输入，以及一套不依赖 WPF/WinForms 的剪贴板原语。
///
/// macOS 端对应 <c>AutoPaste.swift</c>：那边合成按键需要「辅助功能」权限，
/// 只有用户能授予，拒绝后降级为「文件已在剪贴板」。Windows 的
/// <c>SendInput</c>/<c>SetForegroundWindow</c> 不需要任何权限声明——这是两端
/// 唯一不对等的地方，所以 Windows 没有 <c>notTrusted</c> 分支，但
/// <c>SetForegroundWindow</c> 受系统前台锁限制，因此带上
/// <c>AttachThreadInput</c> 作为同等的兜底手段。
/// </summary>
internal static class Win32
{
    internal const int SW_SHOW = 5;
    internal const int SW_RESTORE = 9;

    internal const uint CF_UNICODETEXT = 13;
    internal const uint CF_HDROP = 15;

    internal const uint GMEM_MOVEABLE = 0x0002;

    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_V = 0x56;

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    /// <summary>Returns the owning thread id; the process id is delivered via <paramref name="processId"/>.</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    /// <summary>
    /// Borrows the foreground thread's input queue for one activation.
    /// <c>SetForegroundWindow</c> rejects calls from a process that is not the
    /// foreground process; sharing the input queue is the documented way to be
    /// allowed through, and is exactly what the shell does for its own activations.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    /// <summary>On success the clipboard owns <paramref name="hMem"/> — do not free it afterwards.</summary>
    [DllImport("user32.dll")]
    internal static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("kernel32.dll")]
    internal static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    internal static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll")]
    internal static extern nint GlobalFree(nint hMem);

    /// <summary>
    /// One Ctrl+V. The macOS original posted ⌘V through the HID event tap so it
    /// arrived like a physical press; <c>SendInput</c> is the same promise on
    /// Windows — it lands in the real input stream, not a message queue the
    /// target might not pump.
    /// </summary>
    internal static bool SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, keyUp: false),
            Key(VK_V, keyUp: false),
            Key(VK_V, keyUp: true),
            Key(VK_CONTROL, keyUp: true),
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>
    /// Restores the window if it is minimized, then asks for the foreground.
    /// Mirrors <c>restoreWindows</c> in AutoPaste.swift but far simpler: Windows
    /// only ever raises the window we are handed, never enumerates and unminimizes
    /// a whole app's window list — the same 「只动用户正看着的那一个窗口」 rule.
    /// </summary>
    internal static bool BringToForeground(nint hwnd)
    {
        if (hwnd == 0)
            return false;
        ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var current = GetCurrentThreadId();
        var attached = foregroundThread != 0
            && foregroundThread != current
            && AttachThreadInput(current, foregroundThread, true);
        try
        {
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(current, foregroundThread, false);
        }
    }

    private static INPUT Key(ushort virtualKey, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = 0,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = 0,
            },
        },
    };

    // The union must carry MOUSEINPUT even though only keyboard input is sent:
    // SendInput sizes INPUT by its largest member (32 bytes on x64), and a
    // struct declared with only KEYBDINPUT would be handed a wrong cbSize.
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
