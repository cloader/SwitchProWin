using System.Runtime.InteropServices;

namespace SwitchPro.Engine;

/// <summary>相对鼠标移动（体感瞄准用）。SendInput 注入，对游戏/桌面通用。</summary>
internal static class MouseMover
{
    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public nint pad1;
        [FieldOffset(0)] public nint pad2;
        [FieldOffset(0)] public nint pad3;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>相对移动鼠标光标。</summary>
    public static void MoveRelative(int dx, int dy)
    {
        var input = new INPUT
        {
            type = InputMouse,
            u = new InputUnion
            {
                mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MouseEventMove },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }
}
