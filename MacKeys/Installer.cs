using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MacKeys;

/// <summary>
/// MacKeys installs itself: the release file "MacKeys-x.y.z-Instalador.exe" is the app, and when it runs from
/// outside the install folder it opens the installer window instead. Everything is per user (no admin):
/// %LOCALAPPDATA%\Programs\MacKeys, a Start menu shortcut and an entry in Settings › Apps › Installed apps.
/// </summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MacKeys";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MacKeys");

    public static string InstalledExe => Path.Combine(InstallDir, "MacKeys.exe");

    private static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MacKeys.lnk");

    public static string LogFile => Path.Combine(Path.GetTempPath(), "MacKeys-instalacao.log");

    public static string CurrentVersion => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "?";

    public static bool RunningFromInstallDir => string.Equals(
        Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>The installer opens for --install, or for a copy named like the release file ("…Instalador.exe").</summary>
    public static bool ShouldRunInstaller(string[] args)
    {
        if (args.Contains("--install")) return true;
        if (args.Length > 0 || RunningFromInstallDir) return false;
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        return name.Contains("instal", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("setup", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Version already installed, if any ("0.4.1").</summary>
    public static string? InstalledVersion
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("DisplayVersion") is string version) return version;
            if (!File.Exists(InstalledExe)) return null;
            var info = FileVersionInfo.GetVersionInfo(InstalledExe);
            return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
    }

    /// <summary>Language picked on the installer's first screen, saved into the settings when installing.</summary>
    public static AppLanguage? ChosenLanguage { get; set; }

    internal sealed record Options(bool QuickLook, bool StartWithWindows, bool StartMenu,
        bool RemovePowerToysRemaps, bool DisablePeek, bool UseQuickLook);

    /// <summary>Installs or updates. Problems that don't stop MacKeys from working come back as warnings.</summary>
    public static async Task<List<string>> InstallAsync(Options options, IProgress<(string Status, double Fraction)> progress)
    {
        var warnings = new List<string>();
        Log($"──── Instalação {CurrentVersion} ({DateTime.Now:yyyy-MM-dd HH:mm}) de {Environment.ProcessPath}");

        progress.Report((L.T("Fechando o MacKeys…"), 0.02));
        await Task.Run(() =>
        {
            QuickLookCompanion.Stop();
            CloseOtherMacKeys();
        });

        progress.Report((L.T("Copiando o MacKeys…"), 0.06));
        await Task.Run(CopySelf);

        progress.Report((L.T("Criando atalhos…"), 0.1));
        await Task.Run(() =>
        {
            if (options.StartMenu) CreateShortcut();
            else File.Delete(StartMenuShortcut);
            Startup.SetEnabled(options.StartWithWindows, InstalledExe);
            Register();
        });
        Log("App copiado, atalho e registro prontos");

        if (options.RemovePowerToysRemaps || options.DisablePeek)
        {
            progress.Report((L.T("Ajustando o PowerToys…"), 0.13));
            string? warning = await Task.Run(() => FixPowerToys(options));
            if (warning != null) warnings.Add(warning);
        }

        // MacKeys' own settings: Quick Look on only when QuickLook is the one answering Space.
        await Task.Run(() =>
        {
            var settings = Settings.Load();
            if (ChosenLanguage is { } language) settings.Language = language;
            settings.SetOn(ShortcutId.QuickLook, options.UseQuickLook);
        });

        if (options.QuickLook && options.UseQuickLook)
        {
            if (QuickLookCompanion.IsUpToDate)
            {
                Log("QuickLook já está na versão " + QuickLookCompanion.InstalledVersion);
            }
            else
            {
                try
                {
                    var sub = new Progress<(string Status, double Fraction)>(p =>
                        progress.Report((p.Status, 0.15 + 0.83 * p.Fraction)));
                    await QuickLookCompanion.InstallAsync(sub);
                    Log("QuickLook " + QuickLookCompanion.InstalledVersion + " instalado");
                }
                catch (Exception e)
                {
                    Log($"QuickLook falhou: {e}");
                    warnings.Add(L.F("Não consegui baixar o QuickLook ({0}). Tente de novo em Configurações › Quick Look.", e.Message));
                }
            }
        }

        progress.Report((L.T("Concluído"), 1));
        Log("Concluído" + (warnings.Count > 0 ? " com avisos: " + string.Join(" | ", warnings) : ""));
        return warnings;
    }

    private static string? FixPowerToys(Options options)
    {
        var state = PowerToys.ReadState();
        var (closed, mainExe) = PowerToys.Stop();
        if (!closed)
        {
            Log("PowerToys não fechou (administrador?)");
            return L.T("Não consegui fechar o PowerToys (ele parece estar rodando como administrador), então nada mudou nele. " +
                       "Feche o PowerToys pelo ícone da bandeja e rode o instalador de novo.");
        }
        try
        {
            if (options.RemovePowerToysRemaps && state.RemapFile != null)
                Log("Mapeamentos removidos; cópia em " + PowerToys.RemoveRemaps(state.RemapFile));
            if (options.DisablePeek)
                Log("Peek desligado; cópia em " + PowerToys.SetPeekEnabled(false));
            return null;
        }
        catch (Exception e)
        {
            Log($"PowerToys: {e}");
            return L.F("Não consegui ajustar o PowerToys ({0}).", e.Message);
        }
        finally
        {
            PowerToys.Start(mainExe);
        }
    }

    private static void CloseOtherMacKeys()
    {
        int self = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("MacKeys"))
        {
            using (process)
            {
                if (process.Id == self) continue;
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception) { /* already gone */ }
            }
        }
    }

    private static void CopySelf()
    {
        Directory.CreateDirectory(InstallDir);
        if (RunningFromInstallDir) return;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
                break;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(500); // the old copy may take a moment to let go of the file
            }
        }
        // A downloaded installer carries the "from the internet" mark; the installed app is ours.
        DeleteFile(InstalledExe + ":Zone.Identifier");
    }

    private static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(StartMenuShortcut);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallDir;
            link.Description = L.T("Atalhos do Mac no Windows");
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>The entry in Settings › Apps › Installed apps, with "Uninstall" running our uninstaller.</summary>
    private static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "MacKeys");
        key.SetValue("DisplayVersion", CurrentVersion);
        key.SetValue("Publisher", "Adson Pereira de Souza");
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("URLInfoAbout", "https://github.com/adson-souza/MacKeys");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        long bytes = new FileInfo(InstalledExe).Length;
        if (QuickLookCompanion.IsInstalled) bytes += 280L * 1024 * 1024;
        key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
    }

    // ───────────── uninstall ─────────────

    /// <summary>
    /// "Uninstall" from Windows runs the installed copy, which can't delete itself: it hands over to a copy in
    /// %TEMP% and exits. Returns true when it did (the caller should quit).
    /// </summary>
    public static bool HandOverUninstall()
    {
        if (!RunningFromInstallDir) return false;
        string temp = Path.Combine(Path.GetTempPath(), "MacKeys-Desinstalar.exe");
        File.Copy(Environment.ProcessPath!, temp, overwrite: true);
        var info = new ProcessStartInfo(temp) { UseShellExecute = false };
        foreach (string arg in Environment.GetCommandLineArgs().Skip(1)) info.ArgumentList.Add(arg);
        Process.Start(info);
        return true;
    }

    public static async Task UninstallAsync(bool removeSettings, IProgress<(string Status, double Fraction)> progress)
    {
        Log($"──── Desinstalação ({DateTime.Now:yyyy-MM-dd HH:mm})");
        progress.Report((L.T("Fechando o MacKeys e o QuickLook…"), 0.1));
        await Task.Run(() =>
        {
            QuickLookCompanion.Stop();
            CloseOtherMacKeys();
        });

        progress.Report((L.T("Removendo atalhos…"), 0.3));
        await Task.Run(() =>
        {
            Startup.SetEnabled(false);
            File.Delete(StartMenuShortcut);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        });

        progress.Report((L.T("Apagando os arquivos…"), 0.5));
        await Task.Run(() =>
        {
            for (int attempt = 0; Directory.Exists(InstallDir); attempt++)
            {
                try { Directory.Delete(InstallDir, recursive: true); }
                catch (Exception) when (attempt < 10) { Thread.Sleep(500); }
            }
            TryDelete(Path.Combine(Path.GetTempPath(), "MacKeys-preview"));
            if (removeSettings) TryDelete(Settings.Folder);
        });
        Log("Desinstalado" + (removeSettings ? " (configurações apagadas)" : ""));
        progress.Report((L.T("Concluído"), 1));
    }

    /// <summary>The temporary uninstaller removes itself a few seconds after it closes.</summary>
    public static void ScheduleSelfDelete()
    {
        if (!Path.GetFileName(Environment.ProcessPath!).Equals("MacKeys-Desinstalar.exe", StringComparison.OrdinalIgnoreCase))
            return;
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c ping -n 4 127.0.0.1 >nul & del /f /q \"{Environment.ProcessPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) { Log($"Não apaguei {dir}: {e.Message}"); }
    }

    public static void Log(string line)
    {
        try { File.AppendAllText(LogFile, line + Environment.NewLine); }
        catch (IOException) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "DeleteFileW")]
    private static extern bool DeleteFile(string path);
}
