using System.Runtime.InteropServices;
using static MacKeys.Native;

namespace MacKeys;

/// <summary>Sends synthetic key events tagged so our own hook can ignore them.</summary>
internal static class KeyInjector
{
    // Marker placed in dwExtraInfo of every event we inject ("MACK").
    public static readonly IntPtr Marker = new(0x4D41434B);

    private static readonly HashSet<Keys> ExtendedKeys = new()
    {
        Keys.Left, Keys.Right, Keys.Up, Keys.Down,
        Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown,
        Keys.Insert, Keys.Delete,
        Keys.RControlKey, Keys.RMenu, Keys.LWin, Keys.RWin,
        Keys.PrintScreen, Keys.Divide, Keys.NumLock,
    };

    public static void Down(Keys key) => Send(key, up: false);

    public static void Up(Keys key) => Send(key, up: true);

    /// <summary>Presses modifiers in order, taps the key, releases modifiers in reverse.</summary>
    public static void Tap(Keys key, params Keys[] modifiers)
    {
        foreach (var m in modifiers) Down(m);
        Down(key);
        Up(key);
        for (int i = modifiers.Length - 1; i >= 0; i--) Up(modifiers[i]);
    }

    private static void Send(Keys key, bool up)
    {
        uint flags = 0;
        if (up) flags |= KEYEVENTF_KEYUP;
        if (ExtendedKeys.Contains(key)) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)key,
                    // Real keyboards send a scan code too, and some apps (Adobe's among them) read it.
                    wScan = (ushort)MapVirtualKey((uint)key, MAPVK_VK_TO_VSC),
                    dwFlags = flags,
                    dwExtraInfo = Marker,
                },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}
