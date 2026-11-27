using System.Text;
using Microsoft.Win32;

namespace MacKeys;

/// <summary>
/// Problem reports the person sees and sends themselves: no background telemetry, no server. The report becomes a
/// prefilled GitHub issue (or text to paste anywhere). Technical details never include anything typed in apps:
/// the log only has events like "keyboard ready", and personal paths are replaced before anything is shown.
/// </summary>
internal static class BugReport
{
    public static string CrashFolder => Path.Combine(Settings.Folder, "erros");

    // ───────────── unexpected errors ─────────────

    /// <summary>Saves an unexpected error for the next report. Never throws.</summary>
    public static void SaveCrash(Exception error, string where)
    {
        try
        {
            Directory.CreateDirectory(CrashFolder);
            string file = Path.Combine(CrashFolder, $"erro-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, $"MacKeys {Installer.CurrentVersion} · {where} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{error}");
            DebugLog.Write($"Erro inesperado ({where}): {error.GetType().Name} {error.Message}");
        }
        catch (Exception) { /* nothing more we can do */ }
    }

    /// <summary>The newest error not yet reported, if any.</summary>
    public static string? PendingCrashFile
    {
        get
        {
            try
            {
                return Directory.Exists(CrashFolder)
                    ? Directory.GetFiles(CrashFolder, "erro-*.txt").Where(f => !f.EndsWith(".relatado.txt"))
                        .OrderDescending().FirstOrDefault()
                    : null;
            }
            catch (IOException) { return null; }
        }
    }

    /// <summary>After a report: older errors stay on disk (renamed) but stop being offered.</summary>
    public static void MarkCrashesReported()
    {
        try
        {
            if (!Directory.Exists(CrashFolder)) return;
            foreach (string file in Directory.GetFiles(CrashFolder, "erro-*.txt").Where(f => !f.EndsWith(".relatado.txt")))
                File.Move(file, Path.ChangeExtension(file, ".relatado.txt"), overwrite: true);
        }
        catch (IOException) { }
    }

    // ───────────── the report ─────────────

    /// <summary>Versions, Windows, keyboard, MacKeys' choices, PowerToys and the end of the log, anonymized.</summary>
    public static string TechnicalInfo(Settings settings)
    {
        var text = new StringBuilder();
        text.AppendLine($"- MacKeys: {Installer.CurrentVersion}{(Installer.RunningFromInstallDir ? "" : " " + L.T("(fora da pasta de instalação)"))}");
        text.AppendLine($"- QuickLook: {(QuickLookCompanion.IsInstalled ? QuickLookCompanion.InstalledVersion ?? "?" : L.T("não instalado"))}" +
                        $"{(QuickLookCompanion.IsInstalled ? ", " + L.T(QuickLookCompanion.IsRunning ? "rodando" : "parado") : "")}");
        text.AppendLine($"- Windows: {WindowsVersion()}");
        text.AppendLine(L.F("- Teclado: {0}", KeyboardLayout()));
        text.AppendLine(L.F("- Escala da tela: {0}%", Math.Round(UI.TrayIcon.IconSize / 16.0 * 100)));
        text.AppendLine(L.F("- MacKeys ativo: {0} · Win como ⌥: {1} · ⌘Espaço: {2}", Yes(settings.Enabled), Yes(settings.WinAsOption), settings.CmdSpaceTarget));
        text.AppendLine(L.F("- Atalhos desligados: {0}", settings.DisabledShortcuts.Count == 0 ? L.T("nenhum") : string.Join(", ", settings.DisabledShortcuts)));
        var custom = settings.AppProfiles.Where(p => p.Value != Profile.Default.Name)
            .Select(p => $"{p.Key}={p.Value}").Order();
        text.AppendLine(L.F("- Perfis: {0}", string.Join(", ", custom)));
        text.AppendLine(L.F("- Botão do meio normal em: {0}", string.Join(", ", settings.NativeMiddleClickApps)));
        text.AppendLine($"- PowerToys: {PowerToysSummary()}");

        if (PendingCrashFile is { } crash)
        {
            text.AppendLine();
            text.AppendLine(L.T("Último erro inesperado:"));
            text.AppendLine("```");
            text.AppendLine(Tail(File.ReadAllText(crash), 40));
            text.AppendLine("```");
        }

        text.AppendLine();
        text.AppendLine(L.T("Últimas linhas do log:"));
        text.AppendLine("```");
        try
        {
            text.AppendLine(File.Exists(DebugLog.FilePath) ? Tail(File.ReadAllText(DebugLog.FilePath), 40) : L.T("(vazio)"));
        }
        catch (IOException) { text.AppendLine(L.T("(não consegui ler o log)")); }
        text.AppendLine("```");
        return Anonymize(text.ToString());
    }

    public static string Compose(bool isProblem, string description, string? technical)
    {
        var body = new StringBuilder();
        body.AppendLine(L.T(isProblem ? "### O que aconteceu" : "### Sugestão"));
        body.AppendLine(string.IsNullOrWhiteSpace(description) ? L.T("(sem descrição)") : description.Trim());
        if (technical != null)
        {
            body.AppendLine();
            body.AppendLine(L.T("### Informações técnicas"));
            body.AppendLine(technical.TrimEnd());
        }
        body.AppendLine();
        body.AppendLine(L.T("<sub>Enviado pelo MacKeys › Relatar um problema</sub>"));
        return body.ToString();
    }

    /// <summary>A "new issue" link with title, text and label filled in (kept under browsers' URL limits).</summary>
    public static string IssueUrl(bool isProblem, string title, string body)
    {
        const int MaxBody = 6000;
        if (body.Length > MaxBody)
            body = body[..MaxBody] + "\n" + L.T("…(cortado: use Copiar para o relatório completo)");
        return $"{UpdateChecker.RepositoryUrl}/issues/new" +
               $"?labels={(isProblem ? "bug" : "enhancement")}" +
               $"&title={Uri.EscapeDataString(title)}" +
               $"&body={Uri.EscapeDataString(body)}";
    }

    /// <summary>Replaces the user folder, user name and computer name, so a public issue shows neither.</summary>
    public static string Anonymize(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        foreach (string name in new[] { Environment.UserName, Environment.MachineName })
            if (name.Length >= 3) text = text.Replace(name, "…", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string Yes(bool value) => L.T(value ? "sim" : "não");

    private static string Tail(string text, int lines) =>
        string.Join('\n', text.Split('\n').TakeLast(lines)).TrimEnd();

    private static string WindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = key?.GetValue("ProductName") as string ?? "Windows";
        int build = Environment.OSVersion.Version.Build;
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
        string display = key?.GetValue("DisplayVersion") as string ?? "";
        return $"{product} {display} (build {build}.{key?.GetValue("UBR")})".Replace("  ", " ");
    }

    private static string KeyboardLayout()
    {
        try
        {
            var language = System.Windows.Forms.InputLanguage.CurrentInputLanguage;
            return $"{language.LayoutName} ({language.Culture.Name})";
        }
        catch (Exception) { return "?"; }
    }

    private static string PowerToysSummary()
    {
        try
        {
            var state = PowerToys.ReadState();
            if (!state.Installed) return L.T("não instalado");
            return L.F("Keyboard Manager {0} ({1} mapeamentos), Peek {2}, Paleta de Comandos {3}",
                L.T(state.KeyboardManagerOn ? "ligado" : "desligado"), state.Remaps.Count,
                L.T(state.PeekOn ? "ligado" : "desligado"), L.T(PowerToys.CommandPaletteInstalled ? "instalada" : "não instalada"));
        }
        catch (Exception) { return "?"; }
    }
}
