using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Xml;

namespace MacKeys;

/// <summary>
/// Quick Look in MacKeys is <see href="https://github.com/QL-Win/QuickLook">QuickLook</see>, by the QL-Win
/// contributors (GPL-3.0). Its official portable release is downloaded unmodified into
/// <c>%LOCALAPPDATA%\Programs\MacKeys\QuickLook</c>; MacKeys starts it, hides its tray icon (the ⌘ icon is
/// enough) and closes it on quit. QuickLook listens for Space in File Explorer by itself, so the keyboard hook
/// stays out of it.
/// </summary>
internal static class QuickLookCompanion
{
    /// <summary>Version installed when GitHub can't be asked for the latest one (offline, rate limit).</summary>
    public const string Version = "4.5.0";
    public const string ProjectUrl = "https://github.com/QL-Win/QuickLook";
    public const string LicenseUrl = "https://github.com/QL-Win/QuickLook/blob/master/LICENSE";

    /// <summary>Official release files, smallest first, with the SHA-256 GitHub publishes for each.</summary>
    public static readonly Release Pinned = new(Version, new Download[]
    {
        new($"{ProjectUrl}/releases/download/{Version}/QuickLook-{Version}.7z",
            "d1d578deb75b83ea09255b89382b088d35d38125c0c2b309825b1e20ac962b91", 62_569_358, SevenZip: true),
        // Windows 10's tar.exe may not open .7z: the .zip is twice the size but always extracts.
        new($"{ProjectUrl}/releases/download/{Version}/QuickLook-{Version}.zip",
            "852d8bcccd984e416fc8491ccedb848f0c3472e930ee0d292188b2ea3df524e0", 122_534_056, SevenZip: false),
    });

    internal sealed record Download(string Url, string Sha256, long Size, bool SevenZip);

    /// <summary>A QuickLook release: its version and the portable archives to try, in order.</summary>
    internal sealed record Release(string Version, Download[] Files);

    /// <summary>Size of the next download (the newest release we know of).</summary>
    public static long DownloadSize => (UpdateChecker.QuickLookLatest ?? Pinned).Files[0].Size;

    public static string Folder => Path.Combine(Installer.InstallDir, "QuickLook");
    public static string Exe => Path.Combine(Folder, "QuickLook.exe");
    private static string VersionFile => Path.Combine(Folder, "mackeys-quicklook.version");

    public static bool IsInstalled => File.Exists(Exe);

    public static string? InstalledVersion
    {
        get
        {
            try { return File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : null; }
            catch (IOException) { return null; }
        }
    }

    /// <summary>Installed, and not older than the newest release we know of.</summary>
    public static bool IsUpToDate => IsInstalled &&
        !UpdateChecker.IsNewer((UpdateChecker.QuickLookLatest ?? Pinned).Version, InstalledVersion ?? "0");

    // ───────────── running alongside MacKeys ─────────────

    /// <summary>Starts or stops our QuickLook to match the setting. Safe to call often, from any thread.</summary>
    public static void Sync(bool wanted)
    {
        try
        {
            if (wanted) Start();
            else Stop();
        }
        catch (Exception e)
        {
            DebugLog.Write($"QuickLook: {e.GetType().Name} {e.Message}");
        }
    }

    private static void Start()
    {
        if (!IsInstalled || AnyRunning()) return; // another QuickLook (Store, installer) already answers Space
        ApplyDefaults();
        Process.Start(new ProcessStartInfo(Exe) { WorkingDirectory = Folder, UseShellExecute = false });
        DebugLog.Write("QuickLook iniciado");
    }

    /// <summary>Closes the QuickLook in our folder (not one the user installed separately).</summary>
    public static void Stop()
    {
        var ours = OurProcesses();
        if (ours.Count == 0) return;
        SendPipeMessage("QuickLook.App.PipeMessages.Quit");
        foreach (var process in ours)
        {
            using (process)
            {
                try
                {
                    if (!process.WaitForExit(3000)) process.Kill();
                }
                catch (Exception) { /* already gone */ }
            }
        }
        DebugLog.Write("QuickLook fechado");
    }

    private static bool AnyRunning()
    {
        var all = Process.GetProcessesByName("QuickLook");
        foreach (var p in all) p.Dispose();
        return all.Length > 0;
    }

    private static List<Process> OurProcesses()
    {
        var ours = new List<Process>();
        foreach (var process in Process.GetProcessesByName("QuickLook"))
        {
            string? path = null;
            try { path = process.MainModule?.FileName; } catch (Exception) { }
            if (path != null && path.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) ours.Add(process);
            else process.Dispose();
        }
        return ours;
    }

    /// <summary>QuickLook's own command channel: "message|path|options" on a per-user named pipe.</summary>
    private static void SendPipeMessage(string message)
    {
        try
        {
            string pipe = "QuickLook.App.Pipe." + WindowsIdentity.GetCurrent().User?.Value;
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
            client.Connect(500);
            using var writer = new StreamWriter(client);
            writer.WriteLine($"{message}||");
        }
        catch (Exception) { /* not listening: the process is killed instead */ }
    }

    // ───────────── QuickLook's own options ─────────────

    /// <summary>Portable QuickLook keeps its settings in UserData\QuickLook.config (an XML list of values).</summary>
    public static string DataFolder => Path.Combine(Folder, "UserData");
    private static string ConfigFile => Path.Combine(DataFolder, "QuickLook.config");

    /// <summary>
    /// MacKeys' ⌘ is the only tray icon, and MacKeys decides which QuickLook version is installed (QuickLook's
    /// own update check would offer an installer that lives outside our folder).
    /// </summary>
    private static void ApplyDefaults() =>
        WriteValues(new Dictionary<string, string> { ["ShowTrayIcon"] = "False", ["DisableAutoUpdateCheck"] = "True" });

    public static bool GetOption(string name, bool fallback)
    {
        try
        {
            if (!File.Exists(ConfigFile)) return fallback;
            var doc = new XmlDocument();
            doc.Load(ConfigFile);
            return bool.TryParse(doc.SelectSingleNode($"/Settings/{name}")?.InnerText, out bool value) ? value : fallback;
        }
        catch (Exception) { return fallback; }
    }

    /// <summary>
    /// Changes one of QuickLook's options. QuickLook reads its file once and writes it back on its own, so a
    /// running copy is closed first and started again afterwards.
    /// </summary>
    public static void SetOption(string name, bool value)
    {
        bool wasRunning = OurProcesses() is { Count: > 0 } ours && Dispose(ours);
        if (wasRunning) Stop();
        WriteValues(new Dictionary<string, string> { [name] = value ? "True" : "False" });
        if (wasRunning) Start();
    }

    /// <summary>Closes and reopens QuickLook (when it misbehaves, or after adding a plugin).</summary>
    public static void Restart()
    {
        Stop();
        Start();
    }

    public static bool IsRunning => OurProcesses() is var ours && Dispose(ours);

    private static bool Dispose(List<Process> processes)
    {
        foreach (var p in processes) p.Dispose();
        return processes.Count > 0;
    }

    private static void WriteValues(Dictionary<string, string> values)
    {
        if (!IsInstalled) return;
        var doc = new XmlDocument();
        try
        {
            if (File.Exists(ConfigFile)) doc.Load(ConfigFile);
        }
        catch (XmlException) { doc = new XmlDocument(); }
        var root = doc.SelectSingleNode("/Settings") ?? doc.AppendChild(doc.CreateElement("Settings"))!;
        bool changed = false;
        foreach (var (name, value) in values)
        {
            var node = root.SelectSingleNode(name) ?? root.AppendChild(doc.CreateElement(name))!;
            if (node.InnerText == value) continue;
            node.InnerText = value;
            changed = true;
        }
        if (!changed) return;
        Directory.CreateDirectory(DataFolder);
        doc.Save(ConfigFile);
    }

    // ───────────── installing ─────────────

    /// <summary>
    /// Downloads the official release, checks its SHA-256 and unpacks it into <see cref="Folder"/>, keeping
    /// QuickLook's own settings and plugins (UserData) from a previous version.
    /// </summary>
    /// <param name="progress">Status line and 0–1 progress.</param>
    /// <param name="release">Which release; by default the latest on GitHub, or <see cref="Pinned"/> if it can't be asked.</param>
    public static async Task InstallAsync(IProgress<(string Status, double Fraction)> progress, Release? release = null,
        CancellationToken cancel = default)
    {
        release ??= await UpdateChecker.FetchQuickLookAsync() ?? Pinned;
        string work = Path.Combine(Path.GetTempPath(), "MacKeys-QuickLook-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            string? root = null;
            Exception? lastError = null;
            foreach (var download in release.Files)
            {
                try
                {
                    string archive = Path.Combine(work, Path.GetFileName(download.Url));
                    await DownloadAsync(download, archive, progress, cancel);
                    progress.Report((L.T("Extraindo o QuickLook…"), 0.92));
                    string extracted = Path.Combine(work, download.SevenZip ? "x7z" : "xzip");
                    await Task.Run(() => Extract(download, archive, extracted), cancel);
                    root = FindRoot(extracted) ?? throw new InvalidDataException(L.T("QuickLook.exe não está no arquivo baixado."));
                    break;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    lastError = e;
                    DebugLog.Write($"QuickLook: {Path.GetFileName(download.Url)} falhou: {e.Message}");
                }
            }
            if (root == null) throw lastError ?? new InvalidOperationException(L.T("Não foi possível baixar o QuickLook."));

            progress.Report((L.T("Instalando o QuickLook…"), 0.97));
            await Task.Run(() => Replace(root, release.Version), cancel);
            progress.Report((L.T("QuickLook instalado"), 1));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception) { }
        }
    }

    private static async Task DownloadAsync(Download download, string file,
        IProgress<(string, double)> progress, CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MacKeys-Installer");
        using var response = await http.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? download.Size;

        using var sha = SHA256.Create();
        await using (var source = await response.Content.ReadAsStreamAsync(cancel))
        await using (var target = File.Create(file))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            long lastReport = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                sha.TransformBlock(buffer, 0, read, null, 0);
                done += read;
                if (done - lastReport > 512 * 1024 || done == total)
                {
                    lastReport = done;
                    progress.Report((L.F("Baixando o QuickLook… {0:0} de {1:0} MB", done / 1_048_576.0, total / 1_048_576.0),
                        0.9 * done / Math.Max(total, 1)));
                }
            }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        string hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (hash != download.Sha256)
            throw new InvalidDataException(L.T("O arquivo baixado não confere com o publicado pelo QuickLook (SHA-256)."));
    }

    private static void Extract(Download download, string archive, string target)
    {
        Directory.CreateDirectory(target);
        if (!download.SevenZip)
        {
            ZipFile.ExtractToDirectory(archive, target, overwriteFiles: true);
            return;
        }
        // Windows' own tar (libarchive) reads .7z; no extra tool to ship.
        string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar)) throw new FileNotFoundException("tar.exe não encontrado", tar);
        var info = new ProcessStartInfo(tar) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        info.ArgumentList.Add("-xf");
        info.ArgumentList.Add(archive);
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(target);
        using var process = Process.Start(info)!;
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"tar: {errors.Trim()}");
    }

    /// <summary>The folder that holds QuickLook.exe (the archive may or may not have a top folder).</summary>
    private static string? FindRoot(string extracted)
    {
        if (File.Exists(Path.Combine(extracted, "QuickLook.exe"))) return extracted;
        return Directory.EnumerateDirectories(extracted)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "QuickLook.exe")));
    }

    private static void Replace(string source, string version)
    {
        Stop();
        Directory.CreateDirectory(Folder);
        foreach (var entry in new DirectoryInfo(Folder).EnumerateFileSystemInfos())
        {
            if (entry.Name.Equals("UserData", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry is DirectoryInfo dir) dir.Delete(recursive: true);
            else entry.Delete();
        }
        CopyTree(source, Folder);
        // portable.lock makes QuickLook keep settings next to itself instead of in %APPDATA%.
        string portable = Path.Combine(Folder, "portable.lock");
        if (!File.Exists(portable)) File.WriteAllText(portable, "This file makes QuickLook portable.");
        File.WriteAllText(VersionFile, version);
        ApplyDefaults();
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
