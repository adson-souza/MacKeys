using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static MacKeys.Native;

namespace MacKeys;

/// <summary>
/// `MacKeys.exe --selftest &lt;report.txt&gt;`: runs the real keyboard hook against a small window of its own,
/// types sequences with SendInput and checks what the window actually receives and that no modifier is left
/// held. Uses in-memory settings (the user's file is never touched) with MacKeys.exe in the Creative profile.
/// </summary>
internal sealed class SelfTest
{
    /// <summary>Events carrying this marker are invisible to the hook: they simulate a key-up it missed.</summary>
    public static readonly IntPtr MissMarker = new(0x54455354); // "TEST"

    public static bool Active { get; private set; }

    private readonly List<string> _seen = new();
    private readonly List<string> _report = new();
    private int _failures;

    public static async Task RunAsync(string reportPath)
    {
        Active = true;
        var test = new SelfTest();
        try { await test.RunCasesAsync(); }
        catch (Exception e) { test.Fail($"exceção: {e}"); }
        test._report.Add(test._failures == 0 ? "RESULTADO: TUDO OK" : $"RESULTADO: {test._failures} FALHA(S)");
        File.WriteAllLines(reportPath, test._report);
    }

    private async Task RunCasesAsync()
    {
        var config = new MapperConfig(Array.Empty<ShortcutId>(),
            new Dictionary<string, string> { ["MacKeys.exe"] = Profile.Creative.Name }, winAsOption: false,
            SearchTarget.WindowsSearch);
        var mapper = new KeyMapper(config);
        mapper.Start();

        var box = new TextBox { FontSize = 16, AcceptsReturn = true };
        box.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.ImeProcessed) return;
            _seen.Add($"{Keyboard.Modifiers}+{key}");
            e.Handled = true;
        };
        box.PreviewMouseDown += (_, e) =>
        {
            _seen.Add($"{Keyboard.Modifiers}+Clique");
            e.Handled = true;
        };
        var window = new Window { Title = "MacKeys autoteste", Width = 420, Height = 160, Content = box, Topmost = true };
        window.Show();
        window.Activate();
        box.Focus();
        await Task.Delay(600);
        _report.Add($"janela em primeiro plano: {GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(window).Handle}");

        // 1. ⌘C → Ctrl+C
        await Case("⌘C vira Ctrl+C", "Control+C", async () =>
        {
            await Down(Keys.LMenu); await Tap(Keys.C); await Up(Keys.LMenu);
        });

        // 2. Holding ⌘ must not be "repaired" by the watchdog.
        _seen.Clear();
        await Down(Keys.LMenu); await Tap(Keys.C);
        await Task.Delay(1200);
        bool stillHeld = LogicallyDown(Keys.ControlKey);
        await Up(Keys.LMenu);
        await Task.Delay(150);
        Check("segurar ⌘ 1,2 s não destrava por engano", stillHeld, $"Ctrl virtual durante o ⌘: {stillHeld}");
        Check("soltar ⌘ solta o Ctrl virtual", !LogicallyDown(Keys.ControlKey), "Ctrl ainda apertado");

        // 3. ⌘'s release lost by the hook: the watchdog must release Ctrl by itself.
        _seen.Clear();
        await Down(Keys.LMenu); await Tap(Keys.C);
        Send(Keys.LMenu, up: true, MissMarker);
        await Task.Delay(900);
        Check("soltura do ⌘ perdida é consertada pelo vigia", !LogicallyDown(Keys.ControlKey), "Ctrl continua preso");
        await Tap(Keys.D);
        await Task.Delay(100);
        Check("depois do conserto, D chega sem modificador", _seen.LastOrDefault() == "None+D", $"recebido: {_seen.LastOrDefault()}");

        // 4. Physical Ctrl+Space untouched.
        await Case("Ctrl+Espaço físico chega intacto", "Control+Space", async () =>
        {
            await Down(Keys.LControlKey); await Tap(Keys.Space); await Up(Keys.LControlKey);
        });

        // 5. Ctrl + numeric keypad untouched, and ⌘ + numpad → Ctrl + numpad.
        await Case("Ctrl+NumPad1 chega intacto", "Control+NumPad1", async () =>
        {
            await Down(Keys.LControlKey); await Tap(Keys.NumPad1); await Up(Keys.LControlKey);
        });
        await Case("⌘+NumPad2 vira Ctrl+NumPad2", "Control+NumPad2", async () =>
        {
            await Down(Keys.LMenu); await Tap(Keys.NumPad2); await Up(Keys.LMenu);
        });

        // 6. Creative profile: Win + key → Alt + key; ⌘ + Win + key → Ctrl + Alt + key.
        await Case("Win+K (criativo) vira Alt+K", "Alt+K", async () =>
        {
            await Down(Keys.LWin); await Tap(Keys.K); await Up(Keys.LWin);
        });
        await Case("⌘+Win+K (criativo) vira Ctrl+Alt+K", "Alt, Control+K", async () =>
        {
            await Down(Keys.LMenu); await Down(Keys.LWin); await Tap(Keys.K); await Up(Keys.LWin); await Up(Keys.LMenu);
        });

        // 6b. Mouse: ⌘ + click → Ctrl + click; Win + click → Alt + click (duplicate-drag in Adobe apps).
        var center = box.PointToScreen(new Point(box.ActualWidth / 2, box.ActualHeight / 2));
        SetCursorPos((int)center.X, (int)center.Y);
        await Task.Delay(100);
        await Case("⌘+clique vira Ctrl+clique", "Control+Clique", async () =>
        {
            await Down(Keys.LMenu); await Click(); await Up(Keys.LMenu);
        });
        await Case("Win+clique vira Alt+clique", "Alt+Clique", async () =>
        {
            await Down(Keys.LWin); await Click(); await Up(Keys.LWin);
        });

        // 7. A burst of 300 key events, then nothing may be left held.
        var keys = new[] { Keys.A, Keys.S, Keys.D, Keys.F };
        for (int i = 0; i < 75; i++)
        {
            Send(Keys.LMenu, up: false); Send(keys[i % 4], up: false); Send(keys[i % 4], up: true); Send(Keys.LMenu, up: true);
        }
        await Task.Delay(800);
        foreach (var modifier in new[] { Keys.ControlKey, Keys.Menu, Keys.LWin, Keys.ShiftKey })
            Check($"após rajada: {modifier} solto", !LogicallyDown(modifier), $"{modifier} preso");

        _report.Add("--- log do MacKeys:");
        window.Close();
        mapper.Dispose();
    }

    private async Task Case(string name, string expected, Func<Task> keys)
    {
        _seen.Clear();
        await keys();
        await Task.Delay(150);
        string got = string.Join(" | ", _seen);
        Check(name, _seen.Count == 1 && _seen[0] == expected, $"esperado {expected}, recebido [{got}]");
        foreach (var modifier in new[] { Keys.ControlKey, Keys.Menu, Keys.LWin })
            if (LogicallyDown(modifier)) Fail($"{name}: {modifier} ficou preso");
    }

    private void Check(string name, bool ok, string detail)
    {
        if (ok) _report.Add($"OK    {name}");
        else Fail($"{name}: {detail}");
    }

    private void Fail(string message)
    {
        _failures++;
        _report.Add($"FALHA {message}");
    }

    private static bool LogicallyDown(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static async Task Down(Keys key) { Send(key, up: false); await Task.Delay(40); }

    private static async Task Up(Keys key) { Send(key, up: true); await Task.Delay(40); }

    private static async Task Tap(Keys key) { await Down(key); await Up(key); }

    private static async Task Click()
    {
        foreach (uint flag in new uint[] { 0x0002, 0x0004 }) // MOUSEEVENTF_LEFTDOWN, LEFTUP
        {
            var input = new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } } };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
            await Task.Delay(40);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private static void Send(Keys key, bool up, IntPtr extra = default)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (key is Keys.LWin or Keys.Left or Keys.Right or Keys.Up or Keys.Down) flags |= KEYEVENTF_EXTENDEDKEY;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)key,
                    wScan = (ushort)MapVirtualKey((uint)key, MAPVK_VK_TO_VSC),
                    dwFlags = flags,
                    dwExtraInfo = extra,
                },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}
