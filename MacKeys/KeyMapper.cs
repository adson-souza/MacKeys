using System.Diagnostics;
using System.Runtime.InteropServices;
using static MacKeys.Native;

namespace MacKeys;

/// <summary>
/// Turns the PC keyboard's bottom row into a Mac one:
///   Left Alt (where ⌘ sits)     → Command layer
///   Left Win (where ⌥ sits)     → Option layer (by default Win stays Windows' own key; see <see cref="Settings.WinAsOption"/>)
/// Right Alt is left untouched so AltGr keeps working on ABNT2 and other layouts.
///
/// Everything here runs on the <see cref="HookHost"/> thread; the public methods hand work over to it.
/// </summary>
internal sealed class KeyMapper : IDisposable
{
    // Unassigned virtual key, tapped before releasing an injected Alt (so Windows doesn't treat
    // "Alt down, click, Alt up" as a tap that opens the menu bar) or a Win we take over (so Start doesn't open).
    private const Keys MenuMask = (Keys)0xE8;
    private const int LLKHF_INJECTED = 0x10;

    private readonly HookHost _host;
    private Process? _rawHelper;
    private readonly bool _selfElevated;
    private volatile MapperConfig _config;
    private volatile bool _enabled = true;

    // Keys whose key-down we swallowed (→ when); their key-up must be swallowed too.
    private readonly Dictionary<Keys, long> _swallowedUps = new();

    // Keys we swallowed and re-sent ourselves (after an injected Win) (→ when); we send their key-up too.
    private readonly Dictionary<Keys, long> _resentKeys = new();

    // Command layer state. A null profile means "pass through" (excluded or elevated app).
    private bool _cmdDown;
    private Profile? _cmdProfile;
    private bool _cmdCtrlHeld;   // injected Ctrl held while ⌘ is down (⌘C → Ctrl+C, ⌘+click...)
    private bool _cmdAltHeld;    // injected Alt held for the ⌘Tab app switcher

    // Option layer state
    private bool _optDown;
    private Profile? _optProfile;
    private bool _optUsed;
    private bool _optCtrlHeld;   // injected Ctrl for word navigation (⌥← → Ctrl+←)
    private bool _optWinHeld;    // injected Win for regular Windows shortcuts (Mac-style ⌥ mode)
    private bool _optAltHeld;    // injected Alt (⌥+click, ⌥+drag, ⌘⌥ combos, Creative profile)
    private bool _winNative;     // Win passed through untouched (default mode)
    private bool _winConverted;  // ...until we released it and pressed Alt instead (Win+click, Win+key in Adobe apps)

    private long _cmdDownAt, _optDownAt;
    private string _foregroundApp = "";

    // The physical keyboard as Raw Input reports it, for the watchdog. Keys our hook swallows may never
    // show up there, so the watchdog only acts on what Raw Input *did* see: a release that happened
    // after we registered the press, which our hook then missed.
    private readonly bool[] _rawDown = new bool[256];
    private readonly long[] _rawAt = new long[256];
    private bool _rawWorks;
    private long _lastHookEvent, _lastRawEvent;

    private readonly Dictionary<uint, (string Exe, bool Elevated, long At)> _processCache = new();

    /// <summary>⌘ went down (app process name, its profile). Raised on the keyboard thread: marshal before touching UI.</summary>
    public event Action<string, Profile>? CommandPressed;

    /// <summary>⌘ was combined with something or released. Raised on the keyboard thread.</summary>
    public event Action? CommandFinished;

    /// <summary>
    /// ⌘ was pressed in an administrator window (its handle), where Windows blocks MacKeys' keys and the
    /// keyboard is left alone. Raised on the keyboard thread.
    /// </summary>
    public event Action<IntPtr>? CommandInElevatedWindow;

    private bool _foregroundElevated;

    public KeyMapper(MapperConfig config)
    {
        _config = config;
        _selfElevated = IsElevated((uint)Environment.ProcessId);
        _host = new HookHost(KeyboardCallback, MouseCallback, Watchdog, WarmUp);
        // Physical keys from the Raw Input helper process arrive on the hook thread, where the state lives.
        _host.RawKey += (key, isDown) => OnRawKey(new RawKeyboard.RawKey(key, isDown));
        _lastHookEvent = Environment.TickCount64;
    }

    public bool Enabled => _enabled;

    public void Start()
    {
        _host.Start();
        StartRawHelper();
    }

    /// <summary>Starts the Raw Input helper (see <see cref="RawKeyboard"/>); without it the watchdog stays idle.</summary>
    private void StartRawHelper()
    {
        try
        {
            var args = $"--raw-helper {_host.Hwnd.ToInt64()} {Environment.ProcessId}{(SelfTest.Active ? " --test" : "")}";
            _rawHelper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false });
        }
        catch (Exception e)
        {
            DebugLog.Write($"Teclado: auxiliar do vigia não iniciou ({e.Message})");
        }
    }

    /// <summary>New settings take effect on the next key press.</summary>
    public void UpdateConfig(MapperConfig config) => _config = config;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) _host.Invoke(ResetCore);
    }

    /// <summary>Releases every injected modifier and forgets all layer state.</summary>
    public void Reset() => _host.Invoke(ResetCore);

    public void Dispose()
    {
        _host.Invoke(ResetCore);
        try { _rawHelper?.Kill(); } catch (Exception) { /* already gone */ }
        _rawHelper?.Dispose();
        _host.Dispose();
    }

    private bool On(ShortcutId id) => _config.IsOn(id);

    private void ResetCore()
    {
        ReleaseCmdCtrl();
        if (_cmdAltHeld) { KeyInjector.Up(Keys.LMenu); _cmdAltHeld = false; }
        ReleaseOptCtrl();
        ReleaseOptAlt();
        foreach (var key in _resentKeys.Keys) KeyInjector.Up(key);
        _resentKeys.Clear();
        if (_optWinHeld) { KeyInjector.Up(Keys.LWin); _optWinHeld = false; }
        _cmdDown = _optDown = _winNative = _winConverted = false;
        _cmdProfile = _optProfile = null;
        _swallowedUps.Clear();
        if (_middleReplayed) SendMiddle(down: false);
        _middlePending = _middleReplayed = false;
        CommandFinished?.Invoke();
    }

    // ───────────── hook callbacks ─────────────

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (SelfTest.Active && data.dwExtraInfo == SelfTest.MissMarker)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam); // a release the hook "missed"
            if ((data.flags & LLKHF_INJECTED) == 0 || SelfTest.Active) _lastHookEvent = Environment.TickCount64;
            if (_enabled && data.dwExtraInfo != KeyInjector.Marker)
            {
                int msg = wParam.ToInt32();
                bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                try
                {
                    if (HandleKey((Keys)data.vkCode, isDown))
                        return (IntPtr)1; // swallow
                }
                catch (Exception e)
                {
                    // Never let an exception escape a hook: Windows would drop it and keys could stick.
                    DebugLog.Write($"Teclado: erro {e.GetType().Name} {e.Message}");
                    ResetCore();
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled)
        {
            int msg = wParam.ToInt32();
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (data.dwExtraInfo != KeyInjector.Marker)
                {
                    if (HandleMiddleButton(msg, data)) return (IntPtr)1; // swallow
                    if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN
                        or WM_MOUSEWHEEL or WM_MOUSEHWHEEL)
                        HandleMouse();
                }
            }
            catch (Exception e)
            {
                DebugLog.Write($"Mouse: erro {e.GetType().Name} {e.Message}");
            }
        }
        // Mouse input is only swallowed for the middle-button gesture; otherwise MacKeys just makes sure
        // the right modifier is down before the event lands.
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // ───────────── middle click → Task View (Mission Control) ─────────────

    private const int DragThreshold = 8;       // pixels: further than this is a drag, not a click
    private const int LongPressMs = 500;       // held longer without moving: an ordinary middle click

    private bool _middlePending;   // middle button down swallowed; deciding between click and drag
    private bool _middleReplayed;  // turned out to be a drag: we re-sent the press, its release goes through
    private int _middleX, _middleY;
    private long _middleAt;

    /// <summary>
    /// A quick middle click opens Task View, like Mission Control on a Mac mouse. Dragging with the middle
    /// button (panning in Figma, autoscroll in browsers) is given back to the app as a normal press, and apps
    /// listed as exceptions (Revit, AutoCAD) never see any change.
    /// </summary>
    /// <returns>true to swallow the event.</returns>
    private bool HandleMiddleButton(int msg, MSLLHOOKSTRUCT data)
    {
        if (msg == WM_MBUTTONDOWN)
        {
            if (!On(ShortcutId.MiddleClickTaskView) || _cmdDown || _optDown || !MiddleClickApplies(data)) return false;
            _middlePending = true;
            _middleX = data.x;
            _middleY = data.y;
            _middleAt = Environment.TickCount64;
            return true;
        }

        if (_middlePending)
        {
            bool moved = Math.Abs(data.x - _middleX) > DragThreshold || Math.Abs(data.y - _middleY) > DragThreshold;
            if (msg == WM_MBUTTONUP)
            {
                _middlePending = false;
                if (Environment.TickCount64 - _middleAt < LongPressMs)
                {
                    _host.Invoke(() => KeyInjector.Tap(Keys.Tab, Keys.LWin)); // Task View
                }
                else
                {
                    // A slow press is just a middle click (e.g. browser autoscroll): replay it.
                    SendMiddle(down: true);
                    SendMiddle(down: false);
                }
                return true;
            }
            if ((msg == WM_MOUSEMOVE && moved) || msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MOUSEWHEEL)
            {
                // A drag (or something else entirely): give the app its middle press after all.
                _middlePending = false;
                _middleReplayed = true;
                SendMiddle(down: true);
            }
            return false;
        }

        if (_middleReplayed && msg == WM_MBUTTONUP)
            _middleReplayed = false; // the real release matches the press we re-sent
        return false;
    }

    /// <summary>The middle click opens Task View unless the window under the cursor belongs to an exception.</summary>
    private bool MiddleClickApplies(MSLLHOOKSTRUCT data)
    {
        IntPtr window = GetAncestor(WindowFromPoint(new POINT { X = data.x, Y = data.y }), GA_ROOT);
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0) return true;
        var (exe, _, _) = ProcessFor(pid);
        return !_config.KeepsMiddleClick(exe) && _config.ProfileFor(exe) != null;
    }

    private static void SendMiddle(bool down)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
                    dwExtraInfo = KeyInjector.Marker,
                },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Compiles everything the hook callbacks touch and runs the side-effect-free parts once, so the first
    /// real key press answers within Windows' hook timeout.
    /// </summary>
    private void WarmUp()
    {
        var stopwatch = Stopwatch.StartNew();
        var types = new[] { typeof(KeyMapper), typeof(KeyInjector), typeof(Native), typeof(MapperConfig),
            typeof(Profile), typeof(HookHost), typeof(DebugLog) };
        foreach (var type in types.SelectMany(t => t.GetNestedTypes(System.Reflection.BindingFlags.NonPublic |
                     System.Reflection.BindingFlags.Public).Prepend(t)))
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(all))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle); }
                catch (Exception) { /* some methods can't be prepared ahead; they'll compile on use */ }
            }
        }

        // Exercise the P/Invoke stubs and lookups the first key press would otherwise build.
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<KBDLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(new KBDLLHOOKSTRUCT(), buffer, false);
            Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        ResolveProfile();
        _foregroundApp = "";
        IsPhysicallyDown(Keys.ShiftKey);
        MapVirtualKey((uint)Keys.LControlKey, MAPVK_VK_TO_VSC);
        CharFor(Keys.OemOpenBrackets);
        SendInput(0, Array.Empty<INPUT>(), Marshal.SizeOf<INPUT>());
        _config.IsOn(ShortcutId.Search);
        DebugLog.Write($"Teclado pronto (aquecimento em {stopwatch.ElapsedMilliseconds} ms)");
    }

    // ───────────── watchdog: repairs what a missed event would break ─────────────

    private void OnRawKey(RawKeyboard.RawKey raw)
    {
        int vk = (int)raw.Key & 0xFF;
        long now = Environment.TickCount64;
        _rawWorks = true;
        _lastRawEvent = now;
        _rawDown[vk] = raw.IsDown;
        _rawAt[vk] = now;
    }

    /// <summary>Raw Input saw this key released after <paramref name="since"/>, a moment ago.</summary>
    private bool MissedRelease(Keys key, long since, long now)
    {
        int vk = (int)key & 0xFF;
        return !_rawDown[vk] && _rawAt[vk] > since && now - _rawAt[vk] > 150;
    }

    private void Watchdog()
    {
        if (!_rawWorks) return;
        long now = Environment.TickCount64;

        // ⌘ or Win was released but its key-up never reached our hook (timeout, secure desktop, sleep...).
        if (_cmdDown && MissedRelease(Keys.LMenu, _cmdDownAt, now))
        {
            DebugLog.Write("Vigia: ⌘ foi solto, mas o MacKeys não viu; destravando");
            ResetCore();
        }
        if (_optDown && MissedRelease(Keys.LWin, _optDownAt, now))
        {
            DebugLog.Write("Vigia: Win foi solto, mas o MacKeys não viu; destravando");
            ResetCore();
        }

        // Something injected is still held with no layer that explains it.
        if (!_cmdDown && !_optDown && (_cmdCtrlHeld || _cmdAltHeld || _optCtrlHeld || _optAltHeld || _optWinHeld))
        {
            DebugLog.Write("Vigia: modificador virtual sem tecla física; soltando");
            ResetCore();
        }

        // Keys waiting for a release we missed.
        foreach (var (key, since) in _swallowedUps.ToList())
            if (MissedRelease(key, since, now)) _swallowedUps.Remove(key);
        foreach (var (key, since) in _resentKeys.ToList())
        {
            if (!MissedRelease(key, since, now)) continue;
            KeyInjector.Up(key);
            _resentKeys.Remove(key);
        }

        // Windows silently removes a hook that answers too slowly. Re-installing it from here turned out to
        // leave it dead (self-test), so this only reports it; with the hook on its own fast thread it
        // shouldn't happen, and a removed hook means keys simply pass through unmapped (nothing sticks).
        if (!_hookLossReported && _lastRawEvent - _lastHookEvent > 3000 && now - _lastRawEvent < 1000
            && !ForegroundElevated())
        {
            _hookLossReported = true;
            DebugLog.Write("Vigia: o gancho do teclado parece ter parado (teclas físicas sem passar pelo MacKeys)");
        }
    }

    private bool _hookLossReported;

    private bool ForegroundElevated()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid != 0 && !_selfElevated && ProcessFor(pid).Elevated;
    }

    // ───────────── keys ─────────────

    /// <returns>true to swallow the physical event.</returns>
    private bool HandleKey(Keys key, bool isDown)
    {
        if (key == Keys.LMenu) return HandleCmdKey(isDown);
        if (key == Keys.LWin) return HandleOptKey(isDown);

        if (!isDown)
        {
            if (_resentKeys.Remove(key))
            {
                KeyInjector.Up(key);
                return true;
            }
            return _swallowedUps.Remove(key);
        }

        // A fresh press: forget a release we were waiting for (if it was missed, it must not eat this one's).
        // Replace() re-adds it when this press is swallowed again (auto-repeat included).
        _swallowedUps.Remove(key);

        if (_cmdDown && _cmdProfile != null) return HandleCmdCombo(key, _cmdProfile);

        if (_optDown && _optProfile != null)
        {
            if (!_winNative) return HandleOptCombo(key, _optProfile);
            // Win stays Windows' key, except in apps whose Mac versions use ⌥ for shortcuts (Adobe,
            // Autodesk...): there Win + key becomes Alt + key. Arrows stay Win (snapping windows).
            if (_optProfile.Option == OptionMode.Alt && !IsModifier(key) && !IsArrow(key)) ConvertWinToAlt();
        }
        return false;
    }

    /// <summary>⌘ + click/drag/scroll → Ctrl, Win(⌥) + click/drag/scroll → Alt, ⌘⌥ → Ctrl+Alt.</summary>
    private void HandleMouse()
    {
        if (!On(ShortcutId.MouseModifiers)) return;
        if (_cmdDown && _cmdProfile != null)
        {
            CommandFinished?.Invoke();
            if (!_cmdAltHeld) HoldCmdModifiers(); // clicks inside the ⌘Tab switcher stay plain
        }
        else if (_optDown && _optProfile != null)
        {
            if (_winNative)
            {
                ConvertWinToAlt();
            }
            else if (!_optWinHeld)
            {
                _optUsed = true;
                ReleaseOptCtrl();
                HoldOptAlt();
            }
        }
    }

    // ───────────── ⌘ Command layer (Left Alt) ─────────────

    private bool HandleCmdKey(bool isDown)
    {
        if (isDown)
        {
            if (_cmdDown) return _cmdProfile != null; // auto-repeat
            _cmdDown = true;
            _cmdDownAt = Environment.TickCount64;
            _cmdProfile = ResolveProfile();
            if (_cmdProfile != null) CommandPressed?.Invoke(_foregroundApp, _cmdProfile);
            else if (_foregroundElevated) CommandInElevatedWindow?.Invoke(GetForegroundWindow());
            return _cmdProfile != null;
        }

        _cmdDown = false;
        if (_cmdProfile == null) return false;
        CommandFinished?.Invoke();
        ReleaseCmdCtrl();
        if (_cmdAltHeld) { KeyInjector.Up(Keys.LMenu); _cmdAltHeld = false; } // confirms the ⌘Tab choice
        return true;
    }

    private bool HandleCmdCombo(Keys key, Profile profile)
    {
        // App switcher is open: let Tab, Shift+Tab, arrows and Esc reach it untouched.
        if (_cmdAltHeld) return false;
        if (IsModifier(key)) return false;
        CommandFinished?.Invoke();

        bool shift = IsPhysicallyDown(Keys.ShiftKey);
        bool ctrl = IsPhysicallyDown(Keys.LControlKey) || IsPhysicallyDown(Keys.RControlKey);
        bool textArrows = profile.Arrows != ArrowMode.Ctrl && On(ShortcutId.TextNavigation);
        bool lineArrows = profile.Arrows == ArrowMode.Line && On(ShortcutId.TextNavigation);
        bool lineDelete = profile.LineDelete && On(ShortcutId.LineDelete);

        if (profile.FinderShortcuts && On(ShortcutId.FinderShortcuts) && FinderShortcut(key, shift) is { } finder)
            return Replace(key, finder);

        switch (key)
        {
            case Keys.Tab when On(ShortcutId.AppSwitcher): // ⌘Tab → Alt+Tab, held until ⌘ is released
                ReleaseCmdCtrl();
                KeyInjector.Down(Keys.LMenu);
                _cmdAltHeld = true;
                return false;

            case Keys.Escape when _optDown && On(ShortcutId.ForceQuit): // ⌘⌥Esc (Force Quit) → Task Manager
                _optUsed = true;
                DropNativeWin();
                ReleaseOptAlt();
                return Replace(key, () => KeyInjector.Tap(Keys.Escape, Keys.LControlKey, Keys.LShiftKey));

            case Keys.Q when On(ShortcutId.Quit): // ⌘Q → Alt+F4
                return Replace(key, () => KeyInjector.Tap(Keys.F4, Keys.LMenu));

            case Keys.M when profile.Minimize && On(ShortcutId.Minimize):
            case Keys.H when profile.Minimize && On(ShortcutId.Minimize): // ⌘M / ⌘H → minimize
                return Replace(key, () => ShowWindow(GetForegroundWindow(), SW_MINIMIZE));

            case Keys.Space when ctrl && On(ShortcutId.Emoji): // ⌘⌃Space → emoji picker
                return Replace(key, () => KeyInjector.Tap(Keys.OemPeriod, Keys.LWin));

            case Keys.Space when !ctrl && !profile.CmdSpaceZoom && On(ShortcutId.Search): // ⌘Space → search
                return Replace(key, OpenSearch);

            case Keys.Left when textArrows: // ⌘← → start of line (Shift selects, since it's physically held)
                return Replace(key, () => KeyInjector.Tap(Keys.Home));
            case Keys.Right when textArrows:
                return Replace(key, () => KeyInjector.Tap(Keys.End));
            case Keys.Up when lineArrows: // ⌘↑ → top of document
                return Replace(key, () => KeyInjector.Tap(Keys.Home, Keys.LControlKey));
            case Keys.Down when lineArrows:
                return Replace(key, () => KeyInjector.Tap(Keys.End, Keys.LControlKey));

            case Keys.Back when lineDelete: // ⌘⌫ → delete to start of line
                return Replace(key, () =>
                {
                    KeyInjector.Tap(Keys.Home, Keys.LShiftKey);
                    KeyInjector.Tap(Keys.Back);
                });
            case Keys.Delete when lineDelete: // ⌘⌦ → delete to end of line
                return Replace(key, () =>
                {
                    KeyInjector.Tap(Keys.End, Keys.LShiftKey);
                    KeyInjector.Tap(Keys.Delete);
                });

            case Keys.D3 when shift && On(ShortcutId.Screenshot): // ⌘⇧3 → full screenshot
                return Replace(key, () => KeyInjector.Tap(Keys.PrintScreen));
            case Keys.D4 when shift && On(ShortcutId.ScreenSnip): // ⌘⇧4 / ⌘⇧5 → Snipping Tool (Shift is held)
            case Keys.D5 when shift && On(ShortcutId.ScreenSnip):
                return Replace(key, () => KeyInjector.Tap(Keys.S, Keys.LWin));

            default: // everything else: ⌘X → Ctrl+X, ⌘⌥X → Ctrl+Alt+X
                HoldCmdModifiers();
                return false;
        }
    }

    /// <summary>⌘Space: PowerToys Command Palette (opened by its link, no simulated Win key) or Windows search.</summary>
    private void OpenSearch()
    {
        if (_config.Search == SearchTarget.CommandPalette)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Process.Start(new ProcessStartInfo("x-cmdpal://") { UseShellExecute = true }); }
                catch (Exception e) { DebugLog.Write($"⌘Espaço: Paleta de Comandos não abriu ({e.Message})"); }
            });
            return;
        }
        KeyInjector.Tap(Keys.S, Keys.LWin);
    }

    /// <summary>
    /// Finder's shortcuts in File Explorer. Several are Alt shortcuts in Explorer, which ⌘ (the physical
    /// Alt) would otherwise turn into Ctrl.
    /// </summary>
    private static Action? FinderShortcut(Keys key, bool shift)
    {
        switch (key)
        {
            case Keys.P: // ⌘P / ⌘⇧P → preview pane (Alt+P)
                return () => KeyInjector.Tap(Keys.P, Keys.LMenu);
            case Keys.Up: // ⌘↑ → parent folder
                return () => KeyInjector.Tap(Keys.Up, Keys.LMenu);
            case Keys.Down: // ⌘↓ → open
                return () => KeyInjector.Tap(Keys.Return);
            case Keys.Back when !shift: // ⌘⌫ → move to Recycle Bin. (No ⌘⇧⌫: Shift+Delete would skip the Bin.)
                return () => KeyInjector.Tap(Keys.Delete);
            case Keys.I: // ⌘I → Properties (Get Info)
                return () => KeyInjector.Tap(Keys.Return, Keys.LMenu);
            case Keys.D1 when !shift: // ⌘1 icons
                return () => KeyInjector.Tap(Keys.D2, Keys.LControlKey, Keys.LShiftKey);
            case Keys.D2 when !shift: // ⌘2 list (details)
                return () => KeyInjector.Tap(Keys.D6, Keys.LControlKey, Keys.LShiftKey);
            case Keys.D3 when !shift: // ⌘3 compact list
                return () => KeyInjector.Tap(Keys.D5, Keys.LControlKey, Keys.LShiftKey);
            case Keys.D4 when !shift: // ⌘4 big icons (closest to Gallery)
                return () => KeyInjector.Tap(Keys.D1, Keys.LControlKey, Keys.LShiftKey);
        }

        return CharFor(key) switch
        {
            '[' => () => KeyInjector.Tap(Keys.Left, Keys.LMenu),  // ⌘[ → back
            ']' => () => KeyInjector.Tap(Keys.Right, Keys.LMenu), // ⌘] → forward
            _ => null,
        };
    }

    /// <summary>Holds Ctrl for ⌘, plus Alt if ⌥ is down too (⌘⌥ → Ctrl+Alt).</summary>
    private void HoldCmdModifiers()
    {
        if (!_cmdCtrlHeld)
        {
            KeyInjector.Down(Keys.LControlKey);
            _cmdCtrlHeld = true;
        }
        if (!_optDown || _optProfile == null) return;
        if (_winNative)
        {
            if (_optProfile.Option == OptionMode.Alt) ConvertWinToAlt(); // ⌘⌥ shortcuts in Adobe apps
        }
        else if (!_optWinHeld)
        {
            _optUsed = true;
            ReleaseOptCtrl();
            HoldOptAlt();
        }
    }

    /// <summary>Runs a one-shot replacement for this key (on every auto-repeat too) and swallows it.</summary>
    private bool Replace(Keys key, Action action)
    {
        ReleaseCmdCtrl();
        ReleaseOptCtrl();
        action();
        _swallowedUps[key] = Environment.TickCount64;
        return true;
    }

    private void ReleaseCmdCtrl()
    {
        if (!_cmdCtrlHeld) return;
        KeyInjector.Up(Keys.LControlKey);
        _cmdCtrlHeld = false;
    }

    // ───────────── ⌥ Option layer (Left Win) ─────────────

    private bool HandleOptKey(bool isDown)
    {
        // Default: Win stays Windows' own key (snapping, Start, Win+E...). Windows won't honor Win
        // shortcuts re-sent after intercepting Win, so it goes through, and MacKeys only steps in for
        // Win + click (all apps) and Win + key (apps with the Creative profile), turning them into Alt.
        if (isDown && !_optDown && !_config.WinAsOption)
        {
            _optDown = true;
            _optDownAt = Environment.TickCount64;
            _winNative = true;
            _winConverted = false;
            _optUsed = false;
            _optProfile = ResolveProfile();
            return false;
        }
        if (_winNative)
        {
            // Once converted, Windows already has Win released: swallow its auto-repeat and its release.
            if (isDown) return _winConverted;
            bool swallow = _winConverted;
            _optDown = _winNative = _winConverted = false;
            ReleaseOptAlt();
            return swallow;
        }

        if (isDown)
        {
            if (_optDown) return _optProfile != null; // auto-repeat
            _optDown = true;
            _optDownAt = Environment.TickCount64;
            _optUsed = false;
            _optProfile = ResolveProfile();
            return _optProfile != null;
        }

        _optDown = false;
        if (_optProfile == null) return false;
        ReleaseOptCtrl();
        ReleaseOptAlt();
        if (_optWinHeld)
        {
            KeyInjector.Up(Keys.LWin);
            _optWinHeld = false;
        }
        else if (!_optUsed && !_cmdDown)
        {
            KeyInjector.Tap(Keys.LWin); // tapped alone → open Start, as usual
        }
        return true;
    }

    /// <summary>Releases the real Win (without opening Start) and holds Alt in its place.</summary>
    private void ConvertWinToAlt()
    {
        if (_winConverted) return;
        DropNativeWin();
        HoldOptAlt();
        _optUsed = true;
    }

    /// <summary>Lifts the physically held Win as far as Windows is concerned, without triggering Start.</summary>
    private void DropNativeWin()
    {
        if (!_winNative || _winConverted) return;
        KeyInjector.Tap(MenuMask);
        KeyInjector.Up(Keys.LWin);
        _winConverted = true;
    }

    private bool HandleOptCombo(Keys key, Profile profile)
    {
        if (IsModifier(key)) return false;
        _optUsed = true;

        // ⌥ + arrows not used for words → Win + arrows.
        if (IsArrow(key) && !On(ShortcutId.WordNavigation))
            return SendWithWin(key);

        // Already acting as Win (e.g. Win+Shift+S) or Alt (after ⌥+click): keep going that way.
        if (_optWinHeld) return SendWithWin(key);
        if (_optAltHeld) return false;

        if (profile.Option == OptionMode.Alt)
        {
            HoldOptAlt(); // ⌥X → Alt+X
            return false;
        }

        switch (key)
        {
            case Keys.Left or Keys.Right when On(ShortcutId.WordNavigation):
            case Keys.Back or Keys.Delete when On(ShortcutId.WordDelete):
                // ⌥←/→ jump words, ⌥⌫ deletes a word
                if (!_optCtrlHeld)
                {
                    KeyInjector.Down(Keys.LControlKey);
                    _optCtrlHeld = true;
                }
                return false;

            case Keys.L when On(ShortcutId.LockScreen): // Win+L can't be injected; lock directly
                return Replace(key, () => LockWorkStation());

            default: // any other combo behaves like the normal Win key
                return SendWithWin(key);
        }
    }

    /// <summary>Win + key in Mac-style ⌥ mode: the key is re-sent right after the injected Win.</summary>
    private bool SendWithWin(Keys key)
    {
        if (!_optWinHeld)
        {
            ReleaseOptCtrl();
            ReleaseOptAlt();
            KeyInjector.Down(Keys.LWin);
            _optWinHeld = true;
        }
        KeyInjector.Down(key);
        _resentKeys[key] = Environment.TickCount64;
        return true;
    }

    private void HoldOptAlt()
    {
        if (_optAltHeld) return;
        KeyInjector.Down(Keys.LMenu);
        _optAltHeld = true;
    }

    private void ReleaseOptAlt()
    {
        if (!_optAltHeld) return;
        KeyInjector.Tap(MenuMask);
        KeyInjector.Up(Keys.LMenu);
        _optAltHeld = false;
    }

    private void ReleaseOptCtrl()
    {
        if (!_optCtrlHeld) return;
        KeyInjector.Up(Keys.LControlKey);
        _optCtrlHeld = false;
    }

    // ───────────── helpers ─────────────

    private static bool IsArrow(Keys key) => key is Keys.Left or Keys.Right or Keys.Up or Keys.Down;

    private static bool IsModifier(Keys key) => key is
        Keys.LShiftKey or Keys.RShiftKey or Keys.ShiftKey or
        Keys.LControlKey or Keys.RControlKey or Keys.ControlKey or
        Keys.LMenu or Keys.RMenu or Keys.Menu or
        Keys.LWin or Keys.RWin or Keys.CapsLock;

    /// <summary>
    /// Profile for the foreground app, or null to leave the keyboard alone: excluded apps, and
    /// elevated windows when we aren't elevated ourselves (Windows would block our injected keys).
    /// </summary>
    private Profile? ResolveProfile()
    {
        _foregroundApp = "";
        _foregroundElevated = false;
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        if (pid == 0) return Profile.Default;
        var (exe, elevated, _) = ProcessFor(pid);
        if (elevated && !_selfElevated)
        {
            _foregroundElevated = true;
            return null;
        }
        _foregroundApp = exe;
        return exe.Length == 0 ? Profile.Default : _config.ProfileFor(exe);
    }

    /// <summary>Process name and elevation, cached per process for a minute (this runs on every ⌘ press).</summary>
    private (string Exe, bool Elevated, long At) ProcessFor(uint pid)
    {
        long now = Environment.TickCount64;
        if (_processCache.TryGetValue(pid, out var cached) && now - cached.At < 60_000) return cached;
        var (exe, elevated) = ProcessInfo(pid);
        if (_processCache.Count > 200) _processCache.Clear();
        return _processCache[pid] = (exe, elevated, now);
    }
}
