using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MacKeys.QuickLook;

/// <summary>
/// Turns Word, Excel and PowerPoint files into a cached PDF using the installed Office, invisibly,
/// so Quick Look can show them with the PDF renderer. (Office's preview handlers work in Explorer's
/// preview pane but never paint when hosted by MacKeys.)
/// </summary>
internal static class OfficeConverter
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private const int MsoAutomationSecurityForceDisable = 3; // never run macros from previewed files
    private const string DummyPassword = "MacKeys-no-password"; // protected files fail instead of prompting

    // In %TEMP%: virtualized Office installs can't write to other apps' folders under %LOCALAPPDATA%
    // ("O nome do diretório não é válido"), but they can write here.
    private static string CacheFolder => Path.Combine(Path.GetTempPath(), "MacKeys-preview");

    private enum App { Word, Excel, PowerPoint }

    public static bool CanConvert(string path) => AppFor(path) != null && Type.GetTypeFromProgID(ProgId(AppFor(path)!.Value)) != null;

    private static App? AppFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".rtf" or ".odt" => App.Word,
        ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".ods" => App.Excel,
        ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".odp" => App.PowerPoint,
        _ => null,
    };

    private static string ProgId(App app) => app switch
    {
        App.Word => "Word.Application",
        App.Excel => "Excel.Application",
        _ => "PowerPoint.Application",
    };

    /// <summary>Path of a PDF version of the file, converting it if needed; null if Office couldn't.</summary>
    public static async Task<string?> ToPdfAsync(string path)
    {
        var app = AppFor(path);
        if (app == null) return null;

        var info = new FileInfo(path);
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")));
        string pdf = Path.Combine(CacheFolder, key + ".pdf");
        if (File.Exists(pdf)) return pdf;

        await OneAtATime.WaitAsync();
        try
        {
            if (File.Exists(pdf)) return pdf;
            Directory.CreateDirectory(CacheFolder);
            CleanCache();

            // Office automation is COM: run it on its own STA thread, off the UI thread.
            var done = new TaskCompletionSource<bool>();
            var thread = new Thread(() =>
            {
                try
                {
                    Export(app.Value, info.FullName, pdf);
                    done.TrySetResult(File.Exists(pdf));
                }
                catch (Exception e)
                {
                    DebugLog.Write($"QL: Office não converteu {info.Name}: {e.GetType().Name} {e.Message}");
                    done.TrySetResult(false);
                }
            }) { IsBackground = true, Name = "Office to PDF" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            var finished = await Task.WhenAny(done.Task, Task.Delay(Timeout));
            if (finished != done.Task)
            {
                DebugLog.Write($"QL: Office demorou demais para converter {info.Name}");
                return null;
            }
            return done.Task.Result ? pdf : null;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static void Export(App app, string source, string pdf)
    {
        // PowerPoint is single-instance: if the user has it open we share it, so we must not quit it.
        bool wasRunning = app == App.PowerPoint && Process.GetProcessesByName("POWERPNT").Length > 0;
        dynamic office = Activator.CreateInstance(Type.GetTypeFromProgID(ProgId(app))!)!;
        try
        {
            office.AutomationSecurity = MsoAutomationSecurityForceDisable;
            switch (app)
            {
                case App.Word:
                {
                    office.Visible = false;
                    office.DisplayAlerts = 0; // wdAlertsNone
                    dynamic doc = office.Documents.Open(FileName: source, ConfirmConversions: false, ReadOnly: true,
                        AddToRecentFiles: false, PasswordDocument: DummyPassword, Visible: false);
                    try { doc.ExportAsFixedFormat(OutputFileName: pdf, ExportFormat: 17); } // wdExportFormatPDF
                    finally { doc.Close(SaveChanges: 0); }
                    break;
                }
                case App.Excel:
                {
                    office.Visible = false;
                    office.DisplayAlerts = false;
                    dynamic book = office.Workbooks.Open(Filename: source, UpdateLinks: 0, ReadOnly: true,
                        Password: DummyPassword, AddToMru: false);
                    try { book.ExportAsFixedFormat(Type: 0, Filename: pdf); } // xlTypePDF
                    finally { book.Close(SaveChanges: false); }
                    break;
                }
                case App.PowerPoint:
                {
                    office.DisplayAlerts = 1; // ppAlertsNone
                    dynamic deck = office.Presentations.Open(FileName: source, ReadOnly: -1, Untitled: 0, WithWindow: 0);
                    try { deck.SaveAs(FileName: pdf, FileFormat: 32); } // ppSaveAsPDF
                    finally { deck.Close(); }
                    break;
                }
            }
        }
        finally
        {
            if (!wasRunning)
            {
                try { office.Quit(); } catch (Exception) { /* already gone */ }
            }
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(office);
        }
    }

    /// <summary>Keeps the cache from growing forever: drops previews older than a week.</summary>
    private static void CleanCache()
    {
        foreach (var file in new DirectoryInfo(CacheFolder).EnumerateFiles("*.pdf"))
        {
            if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
            {
                try { file.Delete(); } catch (Exception) { /* in use */ }
            }
        }
    }
}
