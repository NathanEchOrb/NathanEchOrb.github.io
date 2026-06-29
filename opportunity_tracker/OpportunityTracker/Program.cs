using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OpportunityTracker.Pipeline;

namespace OpportunityTracker;

static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--headless", StringComparer.OrdinalIgnoreCase))
        {
            if (!AttachConsole(-1))
                AllocConsole();
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            return RunHeadless(args);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    static int RunHeadless(string[] args)
    {
        void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

        string mode = GetArg(args, "--mode") ?? "push";
        string? inputFile = GetArg(args, "--input-file");
        int fiscalYear = int.TryParse(GetArg(args, "--fiscal-year"), out var fy) ? fy : DateTime.Today.Year;
        bool forceReprocess = args.Contains("--force-reprocess", StringComparer.OrdinalIgnoreCase);
        bool fetch = args.Contains("--fetch", StringComparer.OrdinalIgnoreCase);

        Log("=== Headless mode ===");
        Log($"Mode: {mode}, FY: {fiscalYear}, Force: {forceReprocess}");

        string sourcePath;
        string fileName;

        if (inputFile != null)
        {
            sourcePath = Path.GetFullPath(inputFile);
            fileName = Path.GetFileName(sourcePath);
            if (!File.Exists(sourcePath))
            {
                Log($"Input file not found: {sourcePath}");
                return 1;
            }
            Log($"Using input file: {sourcePath}");
        }
        else if (fetch)
        {
            Log("Fetching from SAM.gov...");
            try
            {
                sourcePath = SamGovScraper.FetchAsync(Log).GetAwaiter().GetResult();
                fileName = Path.GetFileName(sourcePath);
                Log($"Fetched: {fileName}");
            }
            catch (Exception ex)
            {
                Log($"Fetch failed: {ex.Message}");
                return 1;
            }
        }
        else
        {
            Log("Scanning Downloads for latest report...");
            var pattern = new Regex(
                @"^opportunities_\d+days_\d{1,2}-\d{1,2}-\d{2,4}\.html$",
                RegexOptions.IgnoreCase);

            var latest = Directory.GetFiles(PathConfig.DownloadsDir, "*.html")
                .Select(f => new FileInfo(f))
                .Where(fi => pattern.IsMatch(fi.Name) && fi.LastWriteTime >= DateTime.Today.AddDays(-14))
                .OrderByDescending(fi => fi.LastWriteTime)
                .FirstOrDefault();

            if (latest == null)
            {
                Log("No matching report files found in Downloads (last 14 days). Use --fetch to auto-fetch or --input-file to specify one.");
                return 1;
            }

            sourcePath = latest.FullName;
            fileName = latest.Name;
            Log($"Found: {fileName} (modified {latest.LastWriteTime:g})");
        }

        var runner = new PipelineRunner(Log);
        try
        {
            runner.Run(sourcePath, fileName, mode, fiscalYear, forceReprocess);
            Log("=== Headless run complete ===");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Pipeline failed: {ex.Message}");
            Log(ex.StackTrace ?? "");
            return 1;
        }
    }

    static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
