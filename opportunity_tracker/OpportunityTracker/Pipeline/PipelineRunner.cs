using System.Diagnostics;

namespace OpportunityTracker.Pipeline;

public class PipelineRunner
{
    private readonly Action<string> _log;

    public PipelineRunner(Action<string> log)
    {
        _log = log;
    }

    public void Run(string sourcePath, string fileName, string mode,
                    int fiscalYear, bool forceReprocess)
    {
        _log($"=== Pipeline started (mode: {mode}) ===");
        _log($"Source: {fileName}");
        _log($"Fiscal year: {fiscalYear}");

        string rawDocsDir = PathConfig.RawDocsDir;
        string enrichedDocsDir = PathConfig.EnrichedDocsDir;
        string publishDocsDir = PathConfig.PublishDocsDir;
        string tempDocDir = PathConfig.TempDocDir;

        // Step 1: Copy to raw docs
        _log("Copying to raw docs...");
        Directory.CreateDirectory(rawDocsDir);
        string rawDestPath = Path.Combine(rawDocsDir, fileName);

        if (File.Exists(rawDestPath) && !forceReprocess)
        {
            _log("File already exists in raw docs (use Force reprocess to overwrite).");
        }
        else
        {
            File.Copy(sourcePath, rawDestPath, overwrite: true);
            _log($"Copied to: {rawDestPath}");
        }

        // Step 2: Enrichment pipeline
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string enrichedFileName = baseName + "_enriched.html";
        string enrichedPath = Path.Combine(enrichedDocsDir, enrichedFileName);

        if (File.Exists(enrichedPath) && !forceReprocess)
        {
            _log("Enriched file already exists (use Force reprocess to overwrite). Skipping enrichment.");
        }
        else
        {
            _log("Parsing HTML...");
            List<Opportunity> opportunities;
            try
            {
                opportunities = HtmlIngestor.ParseHtmlFile(rawDestPath);
            }
            catch (Exception ex)
            {
                _log($"Error parsing HTML: {ex.Message}");
                return;
            }
            _log($"Parsed {opportunities.Count} opportunities.");

            _log("Initializing OrganizationResolver and BudgetFetcher...");
            OrganizationResolver resolver;
            try
            {
                resolver = new OrganizationResolver(fiscalYear, fetchLiveBudgets: true);
                _log("OrganizationResolver initialized.");
            }
            catch (Exception ex)
            {
                _log($"Warning: OrganizationResolver init error: {ex.Message}");
                resolver = new OrganizationResolver(fiscalYear, fetchLiveBudgets: false);
            }

            _log("Enriching opportunities...");
            int enrichedCount = 0;
            foreach (var opp in opportunities)
            {
                try
                {
                    resolver.EnrichOpportunity(opp);
                    PocExtractor.EnrichOpportunity(opp);
                    enrichedCount++;
                }
                catch (Exception ex)
                {
                    _log($"Warning: Enrichment error for '{opp.Title}': {ex.Message}");
                }
            }
            _log($"Enriched {enrichedCount}/{opportunities.Count} opportunities.");

            _log("Generating enriched HTML...");
            Directory.CreateDirectory(enrichedDocsDir);
            try
            {
                OutputGenerator.GenerateEnrichedHtml(opportunities, enrichedPath);
                _log($"Enriched file saved: {enrichedPath}");
            }
            catch (Exception ex)
            {
                _log($"Error generating output: {ex.Message}");
                return;
            }
        }

        if (mode == "preview")
        {
            _log($"Preview mode - enriched file at: {enrichedPath}");
            try
            {
                Process.Start(new ProcessStartInfo(enrichedPath) { UseShellExecute = true });
            }
            catch { }
            _log("=== Pipeline complete (preview only) ===");
            return;
        }

        // Step 3: Sync enriched files to publish docs
        _log("Syncing enriched files to publish docs...");
        Directory.CreateDirectory(publishDocsDir);
        Directory.CreateDirectory(tempDocDir);

        int copiedCount = 0;
        int skippedCount = 0;

        if (Directory.Exists(enrichedDocsDir))
        {
            foreach (var srcFile in Directory.GetFiles(enrichedDocsDir, "*.html"))
            {
                string srcName = Path.GetFileName(srcFile);
                string destPath = Path.Combine(publishDocsDir, srcName);
                string tempPath = Path.Combine(tempDocDir, srcName);

                if (File.Exists(destPath) || File.Exists(tempPath))
                {
                    if (!forceReprocess)
                    {
                        skippedCount++;
                        continue;
                    }
                }

                File.Copy(srcFile, destPath, overwrite: true);
                copiedCount++;
            }
        }

        _log($"Synced {copiedCount} file(s), skipped {skippedCount} already-published file(s).");

        // Step 3b: Partial report processing
        _log("Processing partial reports...");
        var partialManager = new PartialReportManager();
        foreach (var docFile in Directory.GetFiles(publishDocsDir, "*_enriched.html"))
        {
            try
            {
                partialManager.ProcessNewReport(publishDocsDir, tempDocDir, docFile);
            }
            catch (Exception ex)
            {
                _log($"Warning: Partial report processing error for {Path.GetFileName(docFile)}: {ex.Message}");
            }
        }
        _log("Partial report processing complete.");

        // Step 4: Git commit (and optional push)
        _log("Running git operations...");

        RunGit("add docs/");

        string diffOutput = RunGit("diff --cached --name-only");
        if (string.IsNullOrWhiteSpace(diffOutput))
        {
            _log("No changes to commit.");
        }
        else
        {
            _log($"Changed files:\n{diffOutput}");

            string commitMessage = BuildCommitMessage();
            _log($"Commit message: {commitMessage}");

            string commitResult = RunGit($"commit -m \"{commitMessage}\"");
            _log(commitResult);

            if (mode == "push")
            {
                _log("Pulling latest changes...");
                string pullResult = RunGit("pull --rebase", timeoutSeconds: 60);
                _log(pullResult);

                _log("Pushing to remote...");
                string pushResult = RunGit("push", timeoutSeconds: 60);
                _log(pushResult);
            }
            else
            {
                _log("Skipping push (mode: nopush).");
            }
        }

        _log($"=== Pipeline complete (mode: {mode}) ===");
    }

    public string RunGit(string arguments, int timeoutSeconds = 30)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = PathConfig.PagesRepoDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var process = Process.Start(psi);
            if (process == null)
                return "Failed to start git process.";

            bool exited = process.WaitForExit(timeoutSeconds * 1000);
            if (!exited)
            {
                process.Kill();
                return $"Git timed out after {timeoutSeconds}s (may need credentials — run 'git push' manually in a terminal first to cache them).";
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            string result = stdout;
            if (!string.IsNullOrWhiteSpace(stderr))
                result += (string.IsNullOrEmpty(result) ? "" : "\n") + stderr;

            return result.Trim();
        }
        catch (Exception ex)
        {
            return $"Git error: {ex.Message}";
        }
    }

    public static string BuildCommitMessage()
    {
        var today = DateTime.Today;
        string dateStr = $"{today.Month}-{today.Day}-{today.Year % 100}";

        if (today.DayOfWeek == DayOfWeek.Monday)
            return $"Weekly report added for {dateStr}";
        else
            return $"Report update for {today.DayOfWeek} {dateStr}";
    }
}
