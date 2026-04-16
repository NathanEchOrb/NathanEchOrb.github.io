"""
Helper script called by manage_reports.sh.
Appends all rows from a new mid-week enriched file to the partial report,
then sorts all rows by Updated Date (most recent first).

Usage:
    python _merge_partial.py <partial.html> <new_enriched.html>
"""

import re
import sys
from datetime import datetime
from pathlib import Path

MONTHS = {"Jan":1,"Feb":2,"Mar":3,"Apr":4,"May":5,"Jun":6,
          "Jul":7,"Aug":8,"Sep":9,"Oct":10,"Nov":11,"Dec":12}

DATE_RE = re.compile(r"([A-Z][a-z]{2})\s+(\d{1,2}),\s+(\d{4})")


def parse_updated_date(row):
    """Extract Updated Date from row. Returns datetime or very old date."""
    # Updated Date is the 15th <td> (position varies, but we look for the
    # date in a non-enriched td). Just grab any 'Mon DD, YYYY' pattern.
    for m in DATE_RE.finditer(row):
        month = MONTHS.get(m.group(1))
        if month:
            try:
                return datetime(int(m.group(3)), month, int(m.group(2)))
            except ValueError:
                continue
    return datetime(1900, 1, 1)


def extract_rows(html):
    match = re.search(r"<tbody>(.*?)</tbody>", html, re.DOTALL)
    if not match:
        return []
    body = match.group(1)
    return re.findall(r"<tr(?:\s[^>]*)?>.*?</tr>", body, re.DOTALL)


def main():
    if len(sys.argv) != 3:
        print("Usage: _merge_partial.py <partial> <new_file>", file=sys.stderr)
        sys.exit(1)

    partial_path = Path(sys.argv[1])
    new_path = Path(sys.argv[2])

    partial_html = partial_path.read_text(encoding="utf-8")
    new_html = new_path.read_text(encoding="utf-8")

    partial_rows = extract_rows(partial_html)
    new_rows = extract_rows(new_html)

    # Strip any existing week-divider rows — they'll be regenerated
    data_rows = [
        r for r in (partial_rows + new_rows)
        if 'class="week-divider"' not in r
    ]

    # Sort by Updated Date (most recent first)
    data_rows.sort(key=parse_updated_date, reverse=True)

    combined = "\n".join(data_rows)

    # Replace tbody contents in the partial file
    new_tbody = f"<tbody>\n{combined}\n</tbody>"
    result = re.sub(
        r"<tbody>.*?</tbody>",
        lambda m: new_tbody,
        partial_html,
        count=1,
        flags=re.DOTALL,
    )

    partial_path.write_text(result, encoding="utf-8")
    print(f"Merged {len(new_rows)} rows into {partial_path.name} "
          f"(total rows: {len(data_rows)})")


if __name__ == "__main__":
    main()
