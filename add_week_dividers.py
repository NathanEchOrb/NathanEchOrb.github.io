"""
One-time script to retroactively add week divider rows to existing enriched HTML files.

Processes all enriched HTML files in both the Opportunity enriched docs folder
and the GitHub Pages docs folder.

Usage:
    python add_week_dividers.py             # apply to all files
    python add_week_dividers.py --dry-run   # preview without changing files
"""

import re
import sys
from pathlib import Path
from datetime import datetime, timedelta

ENRICHED_DOCS_DIR = Path(r"C:\Users\NathanBlaylock\Documents\Github\Opportunity\enriched docs")
PUBLISH_DOCS_DIR = Path(r"C:\Users\NathanBlaylock\Documents\Github\NathanEchOrb.github.io\docs")

DIVIDER_CSS = (
    ".week-divider td{background-color:#1a1a2e;border:1px solid #555;"
    "color:#7b8cde;font-weight:bold;font-size:14px;padding:8px 12px;"
    "text-align:center;letter-spacing:0.5px;}"
)

DATE_PATTERN = re.compile(
    r'opportunities_\d+days_(\d{1,2})-(\d{1,2})-(\d{2,4})'
)

# Matches date text in the Updated Date column like "Apr 6, 2026  (4)"
UPDATED_DATE_RE = re.compile(
    r'([A-Z][a-z]{2}\s+\d{1,2},\s+\d{4})'
)

MONTH_MAP = {
    "Jan": 1, "Feb": 2, "Mar": 3, "Apr": 4, "May": 5, "Jun": 6,
    "Jul": 7, "Aug": 8, "Sep": 9, "Oct": 10, "Nov": 11, "Dec": 12,
}


def snap_to_monday(dt):
    return dt - timedelta(days=dt.weekday())


def get_report_monday(filename):
    m = DATE_PATTERN.search(filename)
    if not m:
        return None
    mm, dd, yy = int(m.group(1)), int(m.group(2)), int(m.group(3))
    if yy < 100:
        yy += 2000
    return snap_to_monday(datetime(yy, mm, dd))


def week_label(entry_date):
    entry_monday = snap_to_monday(entry_date)
    week_end = entry_monday + timedelta(days=6)
    fmt = lambda d: d.strftime("%b ") + str(d.day) + d.strftime(", %Y")
    return f"{fmt(entry_monday)} — {fmt(week_end)}"


def parse_friendly_date(text):
    """Parse 'Apr 6, 2026' into a datetime."""
    m = UPDATED_DATE_RE.search(text)
    if not m:
        return None
    try:
        return datetime.strptime(m.group(1).replace("  ", " "), "%b %d, %Y")
    except ValueError:
        return None


def count_columns(html):
    """Count <th> tags in the header row."""
    header_match = re.search(r'<thead>(.*?)</thead>', html, re.DOTALL)
    if not header_match:
        return 17
    return header_match.group(1).count('<th')


def process_file(filepath, dry_run=False):
    """Add week divider rows to an enriched HTML file."""
    html = filepath.read_text(encoding="utf-8")

    if "week-divider" in html:
        return False, "already has dividers"

    report_monday = get_report_monday(filepath.name)
    if not report_monday:
        return False, "could not parse report date"

    num_cols = count_columns(html)

    # Inject CSS before </style>
    if DIVIDER_CSS not in html:
        html = html.replace("</style>", DIVIDER_CSS + "\n</style>")

    # Find all <tr> data rows in <tbody>, parse their Updated Date,
    # and insert divider rows at week boundaries.
    tbody_match = re.search(r'(<tbody>)(.*?)(</tbody>)', html, re.DOTALL)
    if not tbody_match:
        return False, "no tbody found"

    tbody_content = tbody_match.group(2)
    rows = re.findall(r'<tr>.*?</tr>', tbody_content, re.DOTALL)

    if not rows:
        return False, "no data rows found"

    new_rows = []
    prev_label = None

    for row in rows:
        # The Updated Date is the 15th <td> (index 14) in the enriched layout,
        # but easier to just search for a date pattern in the row
        date_dt = None
        cells = re.findall(r'<td[^>]*>(.*?)</td>', row, re.DOTALL)
        for cell in cells:
            parsed = parse_friendly_date(cell)
            if parsed:
                date_dt = parsed
                break

        if date_dt:
            label = week_label(date_dt)
            if label != prev_label:
                divider = (
                    f'<tr class="week-divider">'
                    f'<td colspan="{num_cols}">Week of {label}</td></tr>'
                )
                new_rows.append(divider)
                prev_label = label

        new_rows.append(row)

    new_tbody = "<tbody>\n" + "\n".join(new_rows) + "\n</tbody>"
    html = html[:tbody_match.start()] + new_tbody + html[tbody_match.end():]

    if not dry_run:
        filepath.write_text(html, encoding="utf-8")

    return True, f"{len([r for r in new_rows if 'week-divider' in r])} dividers added"


def main():
    dry_run = "--dry-run" in sys.argv

    dirs = []
    if ENRICHED_DOCS_DIR.exists():
        dirs.append(("Enriched docs", ENRICHED_DOCS_DIR))
    if PUBLISH_DOCS_DIR.exists():
        dirs.append(("Publish docs", PUBLISH_DOCS_DIR))

    for dir_label, dir_path in dirs:
        print(f"\n{'='*60}")
        print(f"Processing: {dir_label} ({dir_path})")
        print(f"{'='*60}")

        files = sorted(
            list(dir_path.glob("opportunities_*_enriched.html"))
            + list(dir_path.glob("opportunities_*_partial.html"))
        )
        if not files:
            print("  No files found.")
            continue

        for f in files:
            changed, msg = process_file(f, dry_run=dry_run)
            prefix = "(dry-run) " if dry_run else ""
            status = "UPDATED" if changed else "SKIPPED"
            print(f"  {prefix}{status}: {f.name} — {msg}")

    print("\nDone!")


if __name__ == "__main__":
    main()
