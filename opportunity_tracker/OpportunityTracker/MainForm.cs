using System.Diagnostics;
using System.Text.RegularExpressions;
using OpportunityTracker.Pipeline;

namespace OpportunityTracker;

public partial class MainForm : Form
{
    // ── Key paths (resolved relative to exe location, overridable via paths.json) ──
    private static readonly string DownloadsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static readonly string PagesRepoDir;
    private static readonly string RawDocsDir;
    private static readonly string EnrichedDocsDir;
    private static readonly string PublishDocsDir;
    private static readonly string TempDocDir;

    static MainForm()
    {
        string exeDir = AppContext.BaseDirectory;
        string settingsPath = Path.Combine(exeDir, "paths.json");

        // Default: exe is in the pages repo root, Opportunity repo is a sibling
        string defaultPagesRepo = exeDir.TrimEnd(Path.DirectorySeparatorChar);
        string defaultOppRepo = Path.Combine(Directory.GetParent(defaultPagesRepo)?.FullName ?? exeDir, "Opportunity");

        string pagesRepo = defaultPagesRepo;
        string oppRepo = defaultOppRepo;

        if (File.Exists(settingsPath))
        {
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                var root = json.RootElement;
                if (root.TryGetProperty("pages_repo", out var p) && p.GetString() is string pr)
                    pagesRepo = pr;
                if (root.TryGetProperty("opportunity_repo", out var o) && o.GetString() is string or2)
                    oppRepo = or2;
            }
            catch { }
        }

        PagesRepoDir = pagesRepo;
        RawDocsDir = Path.Combine(oppRepo, "raw docs");
        EnrichedDocsDir = Path.Combine(oppRepo, "enriched docs");
        PublishDocsDir = Path.Combine(pagesRepo, "docs");
        TempDocDir = Path.Combine(pagesRepo, "temp_doc");
    }

    // ── Filename pattern ───────────────────────────────────────────────────
    private static readonly Regex FilePattern = new(
        @"^opportunities_\d+days_\d{1,2}-\d{1,2}-\d{2,4}\.html$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Controls ───────────────────────────────────────────────────────────
    private readonly Button _btnFetch;
    private readonly Label _lblFetchStatus;
    private readonly ListBox _lstFiles;
    private readonly Button _btnRefresh;
    private readonly Button _btnPreview;
    private readonly Label _lblFileCount;
    private readonly NumericUpDown _nudFiscalYear;
    private readonly CheckBox _chkForceReprocess;
    private readonly Button _btnRunPush;
    private readonly Button _btnRunNoPush;
    private readonly Button _btnTestRun;
    private readonly TextBox _txtLog;

    public MainForm()
    {
        Text = "Opportunity Tracker";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(700, 750);
        MinimumSize = new Size(550, 650);
        AutoScroll = true;

        var mainPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        Controls.Add(mainPanel);

        int y = 12;

        // ── Title ──────────────────────────────────────────────────────────
        var lblTitle = new Label
        {
            Text = "Opportunity Tracker",
            Font = new Font(Font.FontFamily, 14f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(12, y),
        };
        mainPanel.Controls.Add(lblTitle);
        y += lblTitle.Height + 12;

        // ── GroupBox: Step 1 ───────────────────────────────────────────────
        var grpFetch = new GroupBox
        {
            Text = "Step 1: Fetch from SAM.gov",
            Location = new Point(12, y),
            Size = new Size(650, 80),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
        };
        mainPanel.Controls.Add(grpFetch);

        _btnFetch = new Button
        {
            Text = "Fetch from SAM.gov (14 days)",
            BackColor = Color.FromArgb(128, 0, 128),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(12, 24),
            Size = new Size(220, 30),
        };
        _btnFetch.Click += BtnFetch_Click;
        grpFetch.Controls.Add(_btnFetch);

        _lblFetchStatus = new Label
        {
            Text = "",
            AutoSize = true,
            Location = new Point(240, 30),
        };
        grpFetch.Controls.Add(_lblFetchStatus);
        y += grpFetch.Height + 8;

        // ── GroupBox: Step 2 ───────────────────────────────────────────────
        var grpSelect = new GroupBox
        {
            Text = "Step 2: Select Report",
            Location = new Point(12, y),
            Size = new Size(650, 180),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
        };
        mainPanel.Controls.Add(grpSelect);

        _lstFiles = new ListBox
        {
            Location = new Point(12, 24),
            Size = new Size(620, 100),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
        };
        grpSelect.Controls.Add(_lstFiles);

        _btnRefresh = new Button
        {
            Text = "Refresh",
            Location = new Point(12, 130),
            Size = new Size(100, 28),
        };
        _btnRefresh.Click += (_, _) => RefreshFileList();
        grpSelect.Controls.Add(_btnRefresh);

        _btnPreview = new Button
        {
            Text = "Preview Selected",
            Location = new Point(120, 130),
            Size = new Size(130, 28),
        };
        _btnPreview.Click += BtnPreview_Click;
        grpSelect.Controls.Add(_btnPreview);

        _lblFileCount = new Label
        {
            Text = "",
            AutoSize = true,
            Location = new Point(260, 136),
        };
        grpSelect.Controls.Add(_lblFileCount);
        y += grpSelect.Height + 8;

        // ── GroupBox: Options ──────────────────────────────────────────────
        var grpOptions = new GroupBox
        {
            Text = "Options",
            Location = new Point(12, y),
            Size = new Size(650, 70),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
        };
        mainPanel.Controls.Add(grpOptions);

        var lblFy = new Label
        {
            Text = "Fiscal Year:",
            AutoSize = true,
            Location = new Point(12, 28),
        };
        grpOptions.Controls.Add(lblFy);

        _nudFiscalYear = new NumericUpDown
        {
            Minimum = 2020,
            Maximum = 2030,
            Value = 2026,
            Location = new Point(100, 25),
            Size = new Size(70, 24),
        };
        grpOptions.Controls.Add(_nudFiscalYear);

        _chkForceReprocess = new CheckBox
        {
            Text = "Force reprocess",
            AutoSize = true,
            Location = new Point(200, 27),
        };
        grpOptions.Controls.Add(_chkForceReprocess);
        y += grpOptions.Height + 8;

        // ── Panel: Publish buttons ────────────────────────────────────────
        var pnlButtons = new Panel
        {
            Location = new Point(12, y),
            Size = new Size(650, 44),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
        };
        mainPanel.Controls.Add(pnlButtons);

        _btnRunPush = new Button
        {
            Text = "Run and Push",
            BackColor = Color.FromArgb(46, 139, 87),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(0, 4),
            Size = new Size(160, 34),
        };
        _btnRunPush.Click += (_, _) => StartPipeline("push");
        pnlButtons.Controls.Add(_btnRunPush);

        _btnRunNoPush = new Button
        {
            Text = "Run without Push",
            BackColor = Color.FromArgb(30, 90, 180),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(170, 4),
            Size = new Size(170, 34),
        };
        _btnRunNoPush.Click += (_, _) => StartPipeline("nopush");
        pnlButtons.Controls.Add(_btnRunNoPush);

        _btnTestRun = new Button
        {
            Text = "Test Run - Preview Only",
            BackColor = Color.FromArgb(220, 140, 0),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(350, 4),
            Size = new Size(190, 34),
        };
        _btnTestRun.Click += (_, _) => StartPipeline("preview");
        pnlButtons.Controls.Add(_btnTestRun);
        y += pnlButtons.Height + 8;

        // ── GroupBox: Log ──────────────────────────────────────────────────
        var grpLog = new GroupBox
        {
            Text = "Log",
            Location = new Point(12, y),
            Size = new Size(650, 180),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        mainPanel.Controls.Add(grpLog);

        _txtLog = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 9f),
            Location = new Point(12, 22),
            Size = new Size(620, 148),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom,
            WordWrap = false,
        };
        grpLog.Controls.Add(_txtLog);

        // ── Initial load ──────────────────────────────────────────────────
        Load += (_, _) => RefreshFileList();
    }

    // ====================================================================
    //  Logging
    // ====================================================================

    private void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        if (_txtLog.InvokeRequired)
        {
            _txtLog.Invoke(() =>
            {
                _txtLog.AppendText(line);
            });
        }
        else
        {
            _txtLog.AppendText(line);
        }
    }

    // ====================================================================
    //  File list
    // ====================================================================

    private void RefreshFileList()
    {
        _lstFiles.Items.Clear();

        if (!Directory.Exists(DownloadsDir))
        {
            _lblFileCount.Text = "Downloads folder not found";
            return;
        }

        var cutoff = DateTime.Today.AddDays(-14);

        var files = Directory.GetFiles(DownloadsDir, "*.html")
            .Select(f => new FileInfo(f))
            .Where(fi => FilePattern.IsMatch(fi.Name) && fi.LastWriteTime >= cutoff)
            .OrderByDescending(fi => fi.LastWriteTime)
            .ToList();

        foreach (var fi in files)
        {
            string display = fi.Name;
            if (IsAlreadyProcessed(fi.Name))
                display += "  (already processed)";
            _lstFiles.Items.Add(display);
        }

        if (_lstFiles.Items.Count > 0)
            _lstFiles.SelectedIndex = 0;

        _lblFileCount.Text = $"{files.Count} file(s) found";
    }

    private static bool IsAlreadyProcessed(string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);

        // Check raw docs
        string rawPath = Path.Combine(RawDocsDir, fileName);
        if (File.Exists(rawPath)) return true;

        // Check enriched docs
        string enrichedName = baseName + "_enriched.html";
        string enrichedPath = Path.Combine(EnrichedDocsDir, enrichedName);
        if (File.Exists(enrichedPath)) return true;

        return false;
    }

    // ====================================================================
    //  Button handlers
    // ====================================================================

    private void BtnFetch_Click(object? sender, EventArgs e)
    {
        SetButtonsEnabled(false);
        _lblFetchStatus.Text = "Fetching...";

        Task.Run(async () =>
        {
            try
            {
                string resultPath = await SamGovScraper.FetchAsync(Log);
                string fileName = Path.GetFileName(resultPath);

                Log("Opening preview in browser...");
                try
                {
                    Process.Start(new ProcessStartInfo(resultPath) { UseShellExecute = true });
                }
                catch { }

                Invoke(() =>
                {
                    _lblFetchStatus.Text = $"Fetched: {fileName}";
                    RefreshFileList();
                });
            }
            catch (Exception ex)
            {
                Log($"Fetch error: {ex.Message}");
                Invoke(() => _lblFetchStatus.Text = "Fetch failed");
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        });
    }

    private void BtnPreview_Click(object? sender, EventArgs e)
    {
        if (_lstFiles.SelectedItem == null)
        {
            Log("No file selected.");
            return;
        }

        string display = _lstFiles.SelectedItem.ToString()!;
        string fileName = display.Split("  (")[0];
        string fullPath = Path.Combine(DownloadsDir, fileName);

        if (!File.Exists(fullPath))
        {
            Log($"File not found: {fullPath}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            Log($"Opened: {fileName}");
        }
        catch (Exception ex)
        {
            Log($"Error opening file: {ex.Message}");
        }
    }

    // ====================================================================
    //  Pipeline execution
    // ====================================================================

    private void SetButtonsEnabled(bool enabled)
    {
        if (InvokeRequired)
        {
            Invoke(() => SetButtonsEnabled(enabled));
            return;
        }

        _btnFetch.Enabled = enabled;
        _btnRefresh.Enabled = enabled;
        _btnPreview.Enabled = enabled;
        _btnRunPush.Enabled = enabled;
        _btnRunNoPush.Enabled = enabled;
        _btnTestRun.Enabled = enabled;
    }

    private void StartPipeline(string mode)
    {
        if (_lstFiles.SelectedItem == null)
        {
            Log("No file selected. Please select a report file first.");
            return;
        }

        string display = _lstFiles.SelectedItem.ToString()!;
        string fileName = display.Split("  (")[0];
        string sourcePath = Path.Combine(DownloadsDir, fileName);

        if (!File.Exists(sourcePath))
        {
            Log($"File not found: {sourcePath}");
            return;
        }

        int fiscalYear = (int)_nudFiscalYear.Value;
        bool forceReprocess = _chkForceReprocess.Checked;

        SetButtonsEnabled(false);

        Task.Run(() =>
        {
            try
            {
                RunPipeline(sourcePath, fileName, mode, fiscalYear, forceReprocess);
            }
            catch (Exception ex)
            {
                Log($"Pipeline failed: {ex.Message}");
                Log(ex.StackTrace ?? "");
            }
            finally
            {
                SetButtonsEnabled(true);
                Invoke(() => RefreshFileList());
            }
        });
    }

    private void RunPipeline(string sourcePath, string fileName, string mode,
                             int fiscalYear, bool forceReprocess)
    {
        Log($"=== Pipeline started (mode: {mode}) ===");
        Log($"Source: {fileName}");
        Log($"Fiscal year: {fiscalYear}");

        // ── Step 1: Copy to raw docs ──────────────────────────────────────
        Log("Copying to raw docs...");
        Directory.CreateDirectory(RawDocsDir);
        string rawDestPath = Path.Combine(RawDocsDir, fileName);

        if (File.Exists(rawDestPath) && !forceReprocess)
        {
            Log("File already exists in raw docs (use Force reprocess to overwrite).");
        }
        else
        {
            File.Copy(sourcePath, rawDestPath, overwrite: true);
            Log($"Copied to: {rawDestPath}");
        }

        // ── Step 2: Enrichment pipeline ───────────────────────────────────
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string enrichedFileName = baseName + "_enriched.html";
        string enrichedPath = Path.Combine(EnrichedDocsDir, enrichedFileName);

        if (File.Exists(enrichedPath) && !forceReprocess)
        {
            Log("Enriched file already exists (use Force reprocess to overwrite). Skipping enrichment.");
        }
        else
        {
            Log("Parsing HTML...");
            List<Opportunity> opportunities;
            try
            {
                opportunities = HtmlIngestor.ParseHtmlFile(rawDestPath);
            }
            catch (Exception ex)
            {
                Log($"Error parsing HTML: {ex.Message}");
                return;
            }
            Log($"Parsed {opportunities.Count} opportunities.");

            Log("Initializing OrganizationResolver and BudgetFetcher...");
            OrganizationResolver resolver;
            try
            {
                resolver = new OrganizationResolver(fiscalYear, fetchLiveBudgets: true);
                Log("OrganizationResolver initialized.");
            }
            catch (Exception ex)
            {
                Log($"Warning: OrganizationResolver init error: {ex.Message}");
                resolver = new OrganizationResolver(fiscalYear, fetchLiveBudgets: false);
            }

            Log("Enriching opportunities...");
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
                    Log($"Warning: Enrichment error for '{opp.Title}': {ex.Message}");
                }
            }
            Log($"Enriched {enrichedCount}/{opportunities.Count} opportunities.");

            Log("Generating enriched HTML...");
            Directory.CreateDirectory(EnrichedDocsDir);
            try
            {
                OutputGenerator.GenerateEnrichedHtml(opportunities, enrichedPath);
                Log($"Enriched file saved: {enrichedPath}");
            }
            catch (Exception ex)
            {
                Log($"Error generating output: {ex.Message}");
                return;
            }
        }

        if (mode == "preview")
        {
            Log("Preview mode - opening enriched file and stopping.");
            try
            {
                Process.Start(new ProcessStartInfo(enrichedPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"Could not open file: {ex.Message}");
            }
            Log("=== Pipeline complete (preview only) ===");
            return;
        }

        // ── Step 3: Sync enriched files to publish docs ───────────────────
        Log("Syncing enriched files to publish docs...");
        Directory.CreateDirectory(PublishDocsDir);
        Directory.CreateDirectory(TempDocDir);

        int copiedCount = 0;
        int skippedCount = 0;

        if (Directory.Exists(EnrichedDocsDir))
        {
            foreach (var srcFile in Directory.GetFiles(EnrichedDocsDir, "*.html"))
            {
                string srcName = Path.GetFileName(srcFile);
                string destPath = Path.Combine(PublishDocsDir, srcName);
                string tempPath = Path.Combine(TempDocDir, srcName);

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

        Log($"Synced {copiedCount} file(s), skipped {skippedCount} already-published file(s).");

        // ── Step 3b: Run PartialReportManager on newly copied files ───────
        Log("Processing partial reports...");
        var partialManager = new PartialReportManager();
        foreach (var docFile in Directory.GetFiles(PublishDocsDir, "*_enriched.html"))
        {
            try
            {
                partialManager.ProcessNewReport(PublishDocsDir, TempDocDir, docFile);
            }
            catch (Exception ex)
            {
                Log($"Warning: Partial report processing error for {Path.GetFileName(docFile)}: {ex.Message}");
            }
        }
        Log("Partial report processing complete.");

        // ── Step 4: Git commit (and optional push) ────────────────────────
        Log("Running git operations...");

        RunGit("add docs/");

        string diffOutput = RunGit("diff --cached --name-only");
        if (string.IsNullOrWhiteSpace(diffOutput))
        {
            Log("No changes to commit.");
        }
        else
        {
            Log($"Changed files:\n{diffOutput}");

            string commitMessage = BuildCommitMessage();
            Log($"Commit message: {commitMessage}");

            string commitResult = RunGit($"commit -m \"{commitMessage}\"");
            Log(commitResult);

            if (mode == "push")
            {
                Log("Pulling latest changes...");
                string pullResult = RunGit("pull --rebase", timeoutSeconds: 60);
                Log(pullResult);

                Log("Pushing to remote...");
                string pushResult = RunGit("push", timeoutSeconds: 60);
                Log(pushResult);
            }
            else
            {
                Log("Skipping push (mode: nopush).");
            }
        }

        Log($"=== Pipeline complete (mode: {mode}) ===");
    }

    // ====================================================================
    //  Git helpers
    // ====================================================================

    private string RunGit(string arguments, int timeoutSeconds = 30)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = PagesRepoDir,
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

    private static string BuildCommitMessage()
    {
        var today = DateTime.Today;
        string dateStr = $"{today.Month}-{today.Day}-{today.Year % 100}";

        if (today.DayOfWeek == DayOfWeek.Monday)
        {
            return $"Weekly report added for {dateStr}";
        }
        else
        {
            string dayName = today.DayOfWeek.ToString();
            return $"Report update for {dayName} {dateStr}";
        }
    }
}
