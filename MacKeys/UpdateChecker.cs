using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MacKeys;

/// <summary>
/// Asks GitHub for new releases of MacKeys and of QuickLook: one small anonymous request each, at startup and
/// once a day. It only tells; installing waits for a click. Downloads are checked against the SHA-256 that
/// GitHub publishes for every release file.
/// </summary>
internal static class UpdateChecker
{
    public const string Repository = "adson-souza/MacKeys";
    public const string RepositoryUrl = "https://github.com/" + Repository;
    private const string QuickLookRepository = "QL-Win/QuickLook";

    internal sealed record MacKeysRelease(string Version, string Notes, string PageUrl, string InstallerUrl,
        string Sha256, long Size, bool Prerelease);

    /// <summary>A MacKeys release newer than this one, if the last check found one.</summary>
    public static MacKeysRelease? MacKeysUpdate { get; private set; }

    /// <summary>The latest QuickLook release, once a check has seen it.</summary>
    public static QuickLookCompanion.Release? QuickLookLatest { get; private set; }

    public static DateTime? LastCheck { get; private set; }
    public static string? LastError { get; private set; }

    /// <summary>Raised on the calling thread after every check.</summary>
    public static event Action? Checked;

    public static string CurrentVersion => Installer.CurrentVersion;

    public static bool QuickLookUpdateAvailable => QuickLookCompanion.IsInstalled && QuickLookLatest != null &&
        IsNewer(QuickLookLatest.Version, QuickLookCompanion.InstalledVersion ?? "0");

    public static async Task CheckAsync(bool includePrereleases)
    {
        LastError = null;
        try
        {
            QuickLookLatest = await FetchQuickLookAsync() ?? QuickLookLatest; // never throws
            MacKeysUpdate = await FetchMacKeysAsync(includePrereleases);
            LastCheck = DateTime.Now;
            if (MacKeysUpdate != null) DebugLog.Write($"Atualização disponível: MacKeys {MacKeysUpdate.Version}");
            if (QuickLookUpdateAvailable) DebugLog.Write($"Atualização disponível: QuickLook {QuickLookLatest!.Version}");
        }
        catch (Exception e)
        {
            LastError = e switch
            {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "o GitHub não encontrou os lançamentos do MacKeys",
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "o GitHub pediu para esperar um pouco",
                HttpRequestException or TaskCanceledException => "sem conexão com o GitHub",
                _ => e.Message,
            };
            DebugLog.Write($"Atualizações: {e.GetType().Name} {e.Message}");
        }
        Checked?.Invoke();
    }

    private static async Task<MacKeysRelease?> FetchMacKeysAsync(bool includePrereleases)
    {
        using var doc = await GetJsonAsync($"https://api.github.com/repos/{Repository}/releases?per_page=20");
        MacKeysRelease? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            bool prerelease = release.GetProperty("prerelease").GetBoolean();
            if (prerelease && !includePrereleases) continue;
            string version = release.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
            if (!IsNewer(version, CurrentVersion) || (best != null && !IsNewer(version, best.Version))) continue;

            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString()!;
                if (!name.EndsWith("-Instalador.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (Digest(asset) is not { } sha) continue; // never install what can't be verified
                best = new MacKeysRelease(version,
                    release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    release.GetProperty("html_url").GetString()!,
                    asset.GetProperty("browser_download_url").GetString()!,
                    sha, asset.GetProperty("size").GetInt64(), prerelease);
                break;
            }
        }
        return best;
    }

    /// <summary>The latest QuickLook release with portable archives GitHub vouches for; null if it can't be asked.</summary>
    public static async Task<QuickLookCompanion.Release?> FetchQuickLookAsync()
    {
        try
        {
            using var doc = await GetJsonAsync($"https://api.github.com/repos/{QuickLookRepository}/releases/latest");
            string version = doc.RootElement.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
            var files = new List<QuickLookCompanion.Download>();
            foreach (string extension in new[] { ".7z", ".zip" }) // smallest first, .zip if tar can't open .7z
            {
                foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString()!;
                    if (!name.Equals($"QuickLook-{version}{extension}", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Digest(asset) is not { } sha) continue;
                    files.Add(new(asset.GetProperty("browser_download_url").GetString()!, sha,
                        asset.GetProperty("size").GetInt64(), SevenZip: extension == ".7z"));
                }
            }
            return files.Count > 0 ? QuickLookLatest = new QuickLookCompanion.Release(version, files.ToArray()) : null;
        }
        catch (Exception e)
        {
            DebugLog.Write($"QuickLook: versão mais nova indisponível ({e.Message})");
            return null;
        }
    }

    private static string? Digest(JsonElement asset) =>
        asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } value &&
        value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? value[7..].ToLowerInvariant()
            : null;

    private static async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var http = NewClient(TimeSpan.FromSeconds(20));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        await using var stream = await http.GetStreamAsync(url);
        return await JsonDocument.ParseAsync(stream);
    }

    internal static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MacKeys/" + CurrentVersion);
        return http;
    }

    /// <summary>"0.10.0" is newer than "0.9.1"; anything unparsable is never newer.</summary>
    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(Normalize(candidate), out var a) &&
        (!System.Version.TryParse(Normalize(current), out var b) || a > b);

    private static string Normalize(string version)
    {
        string core = version.Split('-', '+', ' ')[0];
        return core.Count(c => c == '.') == 0 ? core + ".0" : core;
    }

    // ───────────── updating MacKeys ─────────────

    /// <summary>
    /// Downloads the new installer, checks it and runs it silently: it closes this MacKeys, replaces it and
    /// opens the new one, keeping the settings.
    /// </summary>
    public static async Task InstallMacKeysAsync(MacKeysRelease release, IProgress<string> progress)
    {
        string folder = Path.Combine(Path.GetTempPath(), "MacKeys-atualizacao");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"MacKeys-{release.Version}-Instalador.exe");

        using (var http = NewClient(TimeSpan.FromMinutes(10)))
        using (var response = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            using var sha = SHA256.Create();
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var target = File.Create(file))
            {
                var buffer = new byte[1 << 16];
                long done = 0, lastReport = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    if (done - lastReport > 1 << 20)
                    {
                        lastReport = done;
                        progress.Report(L.F("Baixando… {0:0} de {1:0} MB", done / 1_048_576.0, release.Size / 1_048_576.0));
                    }
                }
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (Convert.ToHexString(sha.Hash!).ToLowerInvariant() != release.Sha256)
            {
                File.Delete(file);
                throw new InvalidDataException(L.T("o arquivo baixado não confere com o publicado no GitHub"));
            }
        }

        progress.Report(L.T("Instalando…"));
        DebugLog.Write($"Atualizando para {release.Version}");
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        info.ArgumentList.Add("--install");
        info.ArgumentList.Add("--silent");
        Process.Start(info);
    }
}
