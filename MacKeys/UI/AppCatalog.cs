using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MacKeys.UI;

/// <summary>Friendly names and icons for process names like "Photoshop.exe".</summary>
internal static class AppCatalog
{
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Photoshop.exe"] = "Adobe Photoshop",
        ["Illustrator.exe"] = "Adobe Illustrator",
        ["InDesign.exe"] = "Adobe InDesign",
        ["Adobe Premiere Pro.exe"] = "Adobe Premiere Pro",
        ["AfterFX.exe"] = "Adobe After Effects",
        ["Revit.exe"] = "Autodesk Revit",
        ["acad.exe"] = "AutoCAD",
        ["CapCut.exe"] = "CapCut",
        ["WINWORD.EXE"] = "Microsoft Word",
        ["EXCEL.EXE"] = "Microsoft Excel",
        ["POWERPNT.EXE"] = "Microsoft PowerPoint",
        ["mstsc.exe"] = "Área de Trabalho Remota",
        ["vmconnect.exe"] = "Hyper-V",
        ["VirtualBoxVM.exe"] = "VirtualBox",
        ["vmware-vmx.exe"] = "VMware",
        ["parsecd.exe"] = "Parsec",
        ["Notion.exe"] = "Notion",
        ["WhatsApp.Root.exe"] = "WhatsApp",
        ["brave.exe"] = "Brave",
        ["chrome.exe"] = "Google Chrome",
        ["msedge.exe"] = "Microsoft Edge",
        ["Figma.exe"] = "Figma",
        ["explorer.exe"] = "Explorador de Arquivos",
    };

    /// <summary>Letters for the placeholder tile when the app isn't installed on this machine.</summary>
    private static readonly Dictionary<string, string> KnownTiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Photoshop.exe"] = "Ps",
        ["Illustrator.exe"] = "Ai",
        ["InDesign.exe"] = "Id",
        ["Adobe Premiere Pro.exe"] = "Pr",
        ["AfterFX.exe"] = "Ae",
        ["Revit.exe"] = "R",
        ["acad.exe"] = "A",
        ["CapCut.exe"] = "Cc",
        ["mstsc.exe"] = "RD",
        ["vmconnect.exe"] = "Hv",
        ["VirtualBoxVM.exe"] = "VB",
        ["vmware-vmx.exe"] = "Vm",
        ["parsecd.exe"] = "P",
    };

    public static string TileLabel(string exe, string name) =>
        KnownTiles.TryGetValue(exe, out var label) ? label : name.Length > 0 ? name[..1].ToUpperInvariant() : "?";

    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static string DisplayName(string exe, string? path = null)
    {
        if (KnownNames.TryGetValue(exe, out var name)) return L.T(name);
        path ??= FindPath(exe);
        if (path != null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch (Exception) { /* fall back to the file name */ }
        }
        return Path.GetFileNameWithoutExtension(exe);
    }

    public static ImageSource? Icon(string exe, string? path = null)
    {
        if (IconCache.TryGetValue(exe, out var cached)) return cached;
        path ??= FindPath(exe);
        ImageSource? icon = null;
        if (path != null)
        {
            try
            {
                using var gdi = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (gdi != null)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(gdi.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    icon = source;
                }
            }
            catch (Exception) { /* no icon */ }
        }
        IconCache[exe] = icon;
        return icon;
    }

    /// <summary>Full path from a running instance, or from the "App Paths" registry (Office, Adobe...).</summary>
    public static string? FindPath(string exe)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } file) return file;
                }
                catch (Exception) { /* elevated or exiting */ }
            }
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is string registered)
            {
                registered = registered.Trim('"');
                if (File.Exists(registered)) return registered;
            }
        }
        return null;
    }

    public sealed record RunningApp(string Exe, string Name, string? Path, ImageSource? Icon);

    /// <summary>Apps that currently have a visible window, one entry per executable.</summary>
    public static List<RunningApp> RunningApps()
    {
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == self || process.MainWindowHandle == IntPtr.Zero ||
                        string.IsNullOrWhiteSpace(process.MainWindowTitle))
                        continue;
                    string exe = process.ProcessName + ".exe";
                    if (apps.ContainsKey(exe)) continue;
                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch (Exception) { /* elevated */ }
                    apps[exe] = new RunningApp(exe, DisplayName(exe, path), path, Icon(exe, path));
                }
                catch (Exception) { /* process exited */ }
            }
        }
        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>MacKeys' own icon at the largest size available in the .ico.</summary>
    public static BitmapSource AppIcon
    {
        get
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/MacKeys.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
    }
}
