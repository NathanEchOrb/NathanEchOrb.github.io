"""
One-button publish script.

Finds opportunity report files in Downloads, runs the enrichment pipeline,
syncs to the GitHub Pages docs folder, and commits + pushes.

Usage:
    python publish.py    # launches the GUI
"""

import sys
import re
import shutil
import subprocess
import threading
import webbrowser
import tkinter as tk
from tkinter import ttk, messagebox
from pathlib import Path
from datetime import datetime, timedelta

# ── Paths ────────────────────────────────────────────────────────────────────

DOWNLOADS_DIR = Path.home() / "Downloads"
OPPORTUNITY_REPO = Path(r"C:\Users\NathanBlaylock\Documents\Github\Opportunity")
RAW_DOCS_DIR = OPPORTUNITY_REPO / "raw docs"
ENRICHED_DOCS_DIR = OPPORTUNITY_REPO / "enriched docs"
PAGES_REPO = Path(r"C:\Users\NathanBlaylock\Documents\Github\NathanEchOrb.github.io")
PUBLISH_DOCS_DIR = PAGES_REPO / "docs"

SAM_SEARCH_URL = (
    "https://sam.gov/search/?page=1&pageSize=50&sort=-modifiedDate"
    "&sfm%5BsimpleSearch%5D%5BkeywordRadio%5D=ALL"
    "&sfm%5BsimpleSearch%5D%5BkeywordEditorTextarea%5D="
    "(darpa OR afrl OR afwerx OR spacewerx OR mda OR isr OR diu OR ussf"
    " OR ssdp OR rco OR sdacp OR nga OR nro OR nsa OR SpOC OR ssc OR starcom"
    " OR otti) AND (satellite OR spacecraft OR vleo OR orbital OR orbit"
    " OR space OR vehicle) OR apfit OR sda OR stratfi OR (space_force)"
    " OR (Open_Topic)"
    "&sfm%5Bstatus%5D%5Bis_active%5D=true"
)

FILENAME_PATTERN = re.compile(
    r"^opportunities_(\d+days)_(\d{1,2}-\d{1,2}-\d{2,4})\.html$"
)
CUTOFF_DAYS = 14

# Bookmarklet JS adapted for automation: hardcoded to 14 days, returns HTML
# string instead of triggering a download dialog.
SCRAPE_JS = r"""
() => {
    var orgs = ['AFRL','RD','RQR','RS','RV','RVB','RVOP','RVSU','RVSV','RVSW',
        'RX','RY','SB','ARMY','DEVCOM','DARPA','DSO','TTO','DIA','DIU',
        'FTS International','In-Q-Tel','MDA','DT','DV','NASA','Ames','Goddard',
        'JPL','NGA','NOAA','NRO','OSC','OSD','OUSD','I&S','R-E','SCO',
        'SpaceWERX','STRIKEWERX','USAF','AFMC','AFLCMC/LPA','HAF','AF','NASIC',
        'SAF','USN','ONR','USSF','HQ','NSIC','SDA','SFC','SPACEFOR-INDOPAC',
        'SpOC','SSDP','SpRCO','SSC','AATS','BC','COMSO','SSIO','SZ','TIDP',
        'STARCOM','TAP Lab','USSOCOM','1st JSOAC','NSW','USSPACECOM','afwerx',
        'rco','nsa','otti'];

    var days = 14;
    var cutoffDate = new Date();
    cutoffDate.setDate(cutoffDate.getDate() - (days + 1));
    cutoffDate.setHours(0, 0, 0, 0);

    var rows = [];
    rows.push(['Opportunity (Linked)', 'Organization', 'Notice Type',
               'Updated Date', 'Current Response Date', 'Office']);

    document.querySelectorAll('app-opportunity-result').forEach(container => {
        var linkEl = container.querySelector(
            'a.usa-link[href*="/workspace/contract/opp/"]');
        if (!linkEl) return;
        var url = linkEl.href || '';
        var title = linkEl.textContent.trim() || '';
        var noticeType = '', updatedDate = '', responseDate = '', office = '';
        container.querySelectorAll('.sds-field__name').forEach(el => {
            var label = el.textContent.trim();
            var valueEl = el.nextElementSibling;
            var value = valueEl ? valueEl.textContent.trim() : '';
            if (label === 'Notice Type') noticeType = value;
            if (label === 'Updated Date') updatedDate = value;
            if (label.includes('Response Date')) responseDate = value;
            if (label === 'Office') office = value;
        });
        var matchedOrgs = [];
        if (office) {
            var officeLower = office.toLowerCase();
            orgs.forEach(function(org) {
                if (officeLower.includes(org.toLowerCase()))
                    matchedOrgs.push(org);
            });
        }
        var matchedOrgsStr = matchedOrgs.join(', ');
        if (updatedDate) {
            var itemDate = new Date(updatedDate);
            itemDate.setHours(0, 0, 0, 0);
            if (!isNaN(itemDate.getTime()) && itemDate < cutoffDate) return;
        }
        rows.push([url, title, matchedOrgsStr, noticeType,
                   updatedDate, responseDate, office]);
    });

    if (rows.length <= 1) return null;

    var today = new Date();
    var dateStr = (today.getMonth()+1) + '-' + today.getDate() + '-'
                  + String(today.getFullYear()).slice(-2);

    var html = '<!DOCTYPE html><html><head><style>'
        + 'body{background-color:#1e1e1e;color:#e0e0e0;font-family:Arial,'
        + 'sans-serif;margin:20px;}'
        + 'h2{color:#4CAF50;margin-bottom:15px;font-size:20px;}'
        + 'table{border-collapse:collapse;width:100%;font-size:13px;}'
        + 'th,td{border:1px solid #444;padding:4px 8px;text-align:left;}'
        + 'th{background-color:#2d2d2d;color:#4CAF50;font-weight:bold;}'
        + 'tr:nth-child(even){background-color:#252525;}'
        + 'tr:nth-child(odd){background-color:#2a2a2a;}'
        + 'tr:hover{background-color:#333;}'
        + 'a{color:#64b5f6;text-decoration:none;}'
        + 'a:hover{text-decoration:underline;color:#90caf9;}'
        + '</style></head><body>'
        + '<h2>Opportunity Data (Last 14 Days)</h2>'
        + '<table><thead><tr>';

    rows[0].forEach(h => html += '<th>' + h + '</th>');
    html += '</tr></thead><tbody>';
    for (var i = 1; i < rows.length; i++) {
        html += '<tr>'
            + '<td><a href="' + rows[i][0] + '" target="_blank">'
            + rows[i][1] + '</a></td>'
            + '<td>' + rows[i][2] + '</td>'
            + '<td>' + rows[i][3] + '</td>'
            + '<td>' + rows[i][4] + '</td>'
            + '<td>' + rows[i][5] + '</td>'
            + '<td>' + rows[i][6] + '</td></tr>';
    }
    html += '</tbody></table></body></html>';
    return html;
}
"""


# ── Backend Functions ────────────────────────────────────────────────────────

def parse_file_date(fname):
    m = FILENAME_PATTERN.match(fname)
    if not m:
        return None
    parts = m.group(2).split("-")
    if len(parts) != 3:
        return None
    mm, dd, yy = int(parts[0]), int(parts[1]), int(parts[2])
    if yy < 100:
        yy += 2000
    try:
        return datetime(yy, mm, dd)
    except ValueError:
        return None


def find_candidates():
    cutoff = datetime.now() - timedelta(days=CUTOFF_DAYS)
    candidates = []
    for f in DOWNLOADS_DIR.iterdir():
        if not f.is_file():
            continue
        if not FILENAME_PATTERN.match(f.name):
            continue
        file_date = parse_file_date(f.name)
        if file_date and file_date >= cutoff:
            candidates.append(f)
    candidates.sort(key=lambda p: parse_file_date(p.name), reverse=True)
    return candidates


def already_processed(fname):
    raw_exists = (RAW_DOCS_DIR / fname).exists()
    enriched_name = fname.replace(".html", "_enriched.html")
    enriched_exists = (ENRICHED_DOCS_DIR / enriched_name).exists()
    return raw_exists or enriched_exists


def fetch_from_sam(log_fn):
    """Use Playwright to load SAM.gov, run the scrape JS, and save the result."""
    from playwright.sync_api import sync_playwright

    today = datetime.now()
    date_str = f"{today.month}-{today.day}-{str(today.year)[-2:]}"
    filename = f"opportunities_14days_{date_str}.html"
    output_path = DOWNLOADS_DIR / filename

    log_fn("Launching browser...")
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page()

        log_fn("Navigating to SAM.gov search...")
        page.goto(SAM_SEARCH_URL, wait_until="domcontentloaded")

        log_fn("Waiting for results to render...")
        try:
            page.wait_for_selector(
                "app-opportunity-result", timeout=60000
            )
            page.wait_for_timeout(3000)
        except Exception:
            browser.close()
            raise RuntimeError(
                "Timed out waiting for SAM.gov results. "
                "The page may require login or have changed layout."
            )

        log_fn("Scraping opportunity data...")
        html = page.evaluate(SCRAPE_JS)
        browser.close()

    if not html:
        raise RuntimeError("No opportunities matched the 14-day filter.")

    output_path.write_text(html, encoding="utf-8")
    log_fn(f"Saved: {filename}")
    return output_path


def run_enrichment(fiscal_year=2026, force=False):
    sys.path.insert(0, str(OPPORTUNITY_REPO))
    from pipeline.bookmarklet_ingest import process_raw_docs_folder

    return process_raw_docs_folder(
        raw_docs_folder=str(RAW_DOCS_DIR),
        enriched_docs_folder=str(ENRICHED_DOCS_DIR),
        fiscal_year=fiscal_year,
        force_reprocess=force,
    )


def sync_enriched_to_publish():
    PUBLISH_DOCS_DIR.mkdir(parents=True, exist_ok=True)
    copied = []
    for src in ENRICHED_DOCS_DIR.iterdir():
        if src.is_file() and src.suffix == ".html":
            dest = PUBLISH_DOCS_DIR / src.name
            if not dest.exists():
                shutil.copy2(src, dest)
                copied.append(src.name)
    return copied


def git_commit_and_push(push=True):
    def run_git(*args):
        return subprocess.run(
            ["git"] + list(args),
            cwd=str(PAGES_REPO),
            capture_output=True,
            text=True,
        )

    run_git("add", "docs/")

    status = run_git("diff", "--cached", "--name-only")
    if not status.stdout.strip():
        return None, False

    today = datetime.now()
    date_str = f"{today.month}-{today.day}-{str(today.year)[-2:]}"
    if today.weekday() == 0:
        msg = f"Weekly report added for {date_str}"
    else:
        dow = today.strftime("%A")
        msg = f"Report update for {dow} {date_str}"

    run_git("commit", "-m", msg)

    pushed = False
    if push:
        result = run_git("push")
        pushed = result.returncode == 0

    return msg, pushed


# ── GUI ──────────────────────────────────────────────────────────────────────

class PublishApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Publish Opportunity Report")
        self.root.geometry("620x620")
        self.root.resizable(False, False)

        self.selected_file = None
        self.candidates = []
        self.create_widgets()
        self.scan_downloads()

    def create_widgets(self):
        main = ttk.Frame(self.root, padding="15")
        main.pack(fill=tk.BOTH, expand=True)

        ttk.Label(
            main, text="Publish Opportunity Report", font=("Arial", 14, "bold")
        ).pack(pady=(0, 10))

        # ── Step 1: Fetch ────────────────────────────────────────────────
        fetch_frame = ttk.LabelFrame(
            main, text="Step 1: Fetch from SAM.gov", padding="10"
        )
        fetch_frame.pack(fill=tk.X, pady=(0, 8))

        fetch_btn_row = ttk.Frame(fetch_frame)
        fetch_btn_row.pack(fill=tk.X)

        self.btn_fetch = tk.Button(
            fetch_btn_row, text="Fetch from SAM.gov (14 days)",
            command=self.run_fetch,
            bg="#9C27B0", fg="white", font=("Arial", 10, "bold"),
            relief=tk.RAISED, padx=10, pady=4,
        )
        self.btn_fetch.pack(side=tk.LEFT)

        self.btn_preview = tk.Button(
            fetch_btn_row, text="Preview in Browser",
            command=self.preview_file, state=tk.DISABLED,
            bg="#607D8B", fg="white", font=("Arial", 10),
            relief=tk.RAISED, padx=10, pady=4,
        )
        self.btn_preview.pack(side=tk.LEFT, padx=(8, 0))

        self.fetch_status = ttk.Label(
            fetch_frame, text="Or select an existing file below.",
            foreground="gray",
        )
        self.fetch_status.pack(anchor=tk.W, pady=(6, 0))

        # ── Step 2: Select file ──────────────────────────────────────────
        sel_frame = ttk.LabelFrame(
            main, text="Step 2: Select Report", padding="10"
        )
        sel_frame.pack(fill=tk.X, pady=(0, 8))

        self.file_listbox = tk.Listbox(
            sel_frame, height=4, font=("Consolas", 10)
        )
        self.file_listbox.pack(fill=tk.X)
        self.file_listbox.bind("<<ListboxSelect>>", self.on_select)

        list_btn_row = ttk.Frame(sel_frame)
        list_btn_row.pack(fill=tk.X, pady=(5, 0))

        self.file_status = ttk.Label(
            list_btn_row, text="Scanning...", foreground="gray"
        )
        self.file_status.pack(side=tk.LEFT)

        self.btn_refresh = tk.Button(
            list_btn_row, text="Refresh", command=self.scan_downloads,
            font=("Arial", 9), padx=6, pady=2,
        )
        self.btn_refresh.pack(side=tk.RIGHT)

        self.btn_preview_sel = tk.Button(
            list_btn_row, text="Preview Selected",
            command=self.preview_selected, font=("Arial", 9), padx=6, pady=2,
        )
        self.btn_preview_sel.pack(side=tk.RIGHT, padx=(0, 4))

        # ── Options ──────────────────────────────────────────────────────
        opt_frame = ttk.LabelFrame(main, text="Options", padding="10")
        opt_frame.pack(fill=tk.X, pady=(0, 8))

        fy_row = ttk.Frame(opt_frame)
        fy_row.pack(fill=tk.X, pady=2)
        ttk.Label(fy_row, text="Fiscal Year:").pack(side=tk.LEFT)
        self.fy_var = tk.StringVar(value="2026")
        ttk.Spinbox(
            fy_row, from_=2020, to=2030, width=6, textvariable=self.fy_var
        ).pack(side=tk.LEFT, padx=(10, 0))

        self.force_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            opt_frame, text="Force reprocess", variable=self.force_var
        ).pack(anchor=tk.W, pady=2)

        # ── Step 3: Publish buttons ──────────────────────────────────────
        btn_label = ttk.Label(
            main, text="Step 3: Publish", font=("Arial", 10, "bold")
        )
        btn_label.pack(anchor=tk.W, pady=(2, 4))

        btn_frame = ttk.Frame(main)
        btn_frame.pack(fill=tk.X, pady=(0, 8))
        btn_frame.columnconfigure(0, weight=1)
        btn_frame.columnconfigure(1, weight=1)
        btn_frame.columnconfigure(2, weight=1)

        self.btn_push = tk.Button(
            btn_frame, text="Run and Push",
            command=lambda: self.run_publish("push"),
            bg="#4CAF50", fg="white", font=("Arial", 10, "bold"),
            relief=tk.RAISED, padx=10, pady=6,
        )
        self.btn_push.grid(row=0, column=0, padx=4, sticky="ew")

        self.btn_no_push = tk.Button(
            btn_frame, text="Run without Push",
            command=lambda: self.run_publish("no_push"),
            bg="#2196F3", fg="white", font=("Arial", 10, "bold"),
            relief=tk.RAISED, padx=10, pady=6,
        )
        self.btn_no_push.grid(row=0, column=1, padx=4, sticky="ew")

        self.btn_dry = tk.Button(
            btn_frame, text="Test Run - Preview Only",
            command=lambda: self.run_publish("dry_run"),
            bg="#FF9800", fg="white", font=("Arial", 10, "bold"),
            relief=tk.RAISED, padx=10, pady=6,
        )
        self.btn_dry.grid(row=0, column=2, padx=4, sticky="ew")

        self.action_buttons = [
            self.btn_fetch, self.btn_push, self.btn_no_push, self.btn_dry
        ]

        # ── Log ──────────────────────────────────────────────────────────
        log_frame = ttk.LabelFrame(main, text="Log", padding="10")
        log_frame.pack(fill=tk.BOTH, expand=True)

        self.log_text = tk.Text(
            log_frame, height=8, state=tk.DISABLED,
            wrap=tk.WORD, font=("Consolas", 9),
        )
        scroll = ttk.Scrollbar(
            log_frame, orient=tk.VERTICAL, command=self.log_text.yview
        )
        self.log_text.configure(yscrollcommand=scroll.set)
        self.log_text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scroll.pack(side=tk.RIGHT, fill=tk.Y)

    # ── Helpers ──────────────────────────────────────────────────────────

    def log(self, msg):
        self.log_text.configure(state=tk.NORMAL)
        self.log_text.insert(tk.END, msg + "\n")
        self.log_text.see(tk.END)
        self.log_text.configure(state=tk.DISABLED)
        self.root.update_idletasks()

    def clear_log(self):
        self.log_text.configure(state=tk.NORMAL)
        self.log_text.delete(1.0, tk.END)
        self.log_text.configure(state=tk.DISABLED)

    def set_buttons_enabled(self, enabled):
        state = tk.NORMAL if enabled else tk.DISABLED
        for btn in self.action_buttons:
            btn.configure(state=state)

    def scan_downloads(self):
        self.candidates = find_candidates()
        self.file_listbox.delete(0, tk.END)

        if not self.candidates:
            self.file_status.config(
                text="No opportunity files in Downloads (last 2 weeks)",
                foreground="red",
            )
            self.selected_file = None
            return

        for f in self.candidates:
            label = f.name
            if already_processed(f.name):
                label += "  (already processed)"
            self.file_listbox.insert(tk.END, label)

        self.file_listbox.selection_set(0)
        self.selected_file = self.candidates[0]
        count = len(self.candidates)
        self.file_status.config(
            text=f"Found {count} file{'s' if count > 1 else ''}"
                 f" — most recent selected",
            foreground="green",
        )

    def on_select(self, event):
        sel = self.file_listbox.curselection()
        if sel:
            self.selected_file = self.candidates[sel[0]]

    def preview_file(self):
        if self.last_fetched and self.last_fetched.exists():
            webbrowser.open(self.last_fetched.as_uri())

    def preview_selected(self):
        if self.selected_file and self.selected_file.exists():
            webbrowser.open(self.selected_file.as_uri())

    # ── Fetch from SAM.gov ───────────────────────────────────────────────

    last_fetched = None

    def run_fetch(self):
        self.set_buttons_enabled(False)
        self.btn_preview.configure(state=tk.DISABLED)
        self.clear_log()
        t = threading.Thread(target=self._fetch_thread, daemon=True)
        t.start()

    def _fetch_thread(self):
        try:
            result_path = fetch_from_sam(self.log)
            self.last_fetched = result_path

            self.log("")
            self.log(f"Fetch complete: {result_path.name}")
            self.log("Opening preview in browser...")
            webbrowser.open(result_path.as_uri())

            self.root.after(0, self._after_fetch_success)

        except Exception as e:
            self.log(f"Fetch error: {e}")
            self.root.after(
                0, lambda: messagebox.showerror("Fetch Error", str(e))
            )
            self.root.after(0, lambda: self.set_buttons_enabled(True))

    def _after_fetch_success(self):
        self.btn_preview.configure(state=tk.NORMAL)
        self.scan_downloads()
        self.fetch_status.config(
            text=f"Fetched: {self.last_fetched.name} — review in browser, "
                 f"then publish below.",
            foreground="#9C27B0",
        )
        self.set_buttons_enabled(True)

    # ── Publish pipeline ─────────────────────────────────────────────────

    def run_publish(self, mode):
        if not self.selected_file:
            messagebox.showwarning(
                "No file selected", "Select a report file first."
            )
            return

        self.set_buttons_enabled(False)
        self.clear_log()
        t = threading.Thread(
            target=self._publish_thread, args=(mode,), daemon=True
        )
        t.start()

    def _publish_thread(self, mode):
        dry_run = mode == "dry_run"
        do_push = mode == "push"
        mode_label = {
            "push": "Run and Push",
            "no_push": "Run without Push",
            "dry_run": "Test Run",
        }[mode]

        try:
            src = self.selected_file
            self.log(f"Mode: {mode_label}")
            self.log(f"Selected: {src.name}")

            # Step 1: Copy to raw docs
            dest = RAW_DOCS_DIR / src.name
            if dest.exists():
                self.log("  Already in raw docs, skipping copy.")
            elif dry_run:
                self.log(f"  (dry-run) Would copy to: {dest}")
            else:
                shutil.copy2(src, dest)
                self.log("  Copied to raw docs.")

            # Step 2: Run enrichment pipeline
            self.log("")
            self.log("Running enrichment pipeline...")
            if dry_run:
                enriched_name = src.name.replace(".html", "_enriched.html")
                self.log(f"  (dry-run) Would enrich -> {enriched_name}")
            else:
                try:
                    results = run_enrichment(
                        fiscal_year=int(self.fy_var.get()),
                        force=self.force_var.get(),
                    )
                    if results:
                        for r in results:
                            self.log(f"  Generated: {Path(r).name}")
                    else:
                        self.log(
                            "  No new files generated (may already be enriched)."
                        )
                except Exception as e:
                    self.log(f"  Pipeline error: {e}")
                    self.root.after(
                        0,
                        lambda: messagebox.showerror("Pipeline Error", str(e)),
                    )
                    return

            # Step 3: Sync enriched -> publish docs
            self.log("")
            self.log("Syncing to GitHub Pages docs folder...")
            if dry_run:
                enriched_name = src.name.replace(".html", "_enriched.html")
                enriched_path = ENRICHED_DOCS_DIR / enriched_name
                if enriched_path.exists():
                    self.log(
                        f"  (dry-run) Would copy {enriched_name} to docs/"
                    )
                else:
                    self.log(
                        f"  (dry-run) {enriched_name} not found "
                        f"in enriched docs yet."
                    )
            else:
                copied = sync_enriched_to_publish()
                if copied:
                    for name in copied:
                        self.log(f"  Synced: {name}")
                else:
                    self.log("  All files already synced.")

            # Step 4: Git commit and push
            self.log("")
            self.log("Committing to git...")
            if dry_run:
                self.log("  (dry-run) Would stage docs/ and commit.")
                if do_push:
                    self.log("  (dry-run) Would push to remote.")
            else:
                msg, pushed = git_commit_and_push(push=do_push)
                if msg is None:
                    self.log("  No changes to commit.")
                else:
                    self.log(f"  Committed: {msg}")
                    if do_push:
                        if pushed:
                            self.log("  Pushed to remote.")
                        else:
                            self.log(
                                "  Push failed — you may need to push manually."
                            )

            self.log("")
            self.log("Done!")

        except Exception as e:
            self.log(f"Error: {e}")
            self.root.after(
                0, lambda: messagebox.showerror("Error", str(e))
            )
        finally:
            self.root.after(0, lambda: self.set_buttons_enabled(True))


def main():
    root = tk.Tk()
    try:
        style = ttk.Style()
        if "clam" in style.theme_names():
            style.theme_use("clam")
    except Exception:
        pass

    PublishApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
