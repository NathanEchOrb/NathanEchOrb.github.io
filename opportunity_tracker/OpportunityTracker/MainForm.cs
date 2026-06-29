using System.Diagnostics;
using System.Text.RegularExpressions;
using OpportunityTracker.Pipeline;

namespace OpportunityTracker;

public partial class MainForm : Form
{

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

        if (!Directory.Exists(PathConfig.DownloadsDir))
        {
            _lblFileCount.Text = "Downloads folder not found";
            return;
        }

        var cutoff = DateTime.Today.AddDays(-14);

        var files = Directory.GetFiles(PathConfig.DownloadsDir, "*.html")
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
        string rawPath = Path.Combine(PathConfig.RawDocsDir, fileName);
        if (File.Exists(rawPath)) return true;

        // Check enriched docs
        string enrichedName = baseName + "_enriched.html";
        string enrichedPath = Path.Combine(PathConfig.EnrichedDocsDir, enrichedName);
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
        string fullPath = Path.Combine(PathConfig.DownloadsDir, fileName);

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
        string sourcePath = Path.Combine(PathConfig.DownloadsDir, fileName);

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
        var runner = new PipelineRunner(Log);
        runner.Run(sourcePath, fileName, mode, fiscalYear, forceReprocess);
    }
}
