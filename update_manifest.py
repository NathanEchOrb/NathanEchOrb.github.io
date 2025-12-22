#!/usr/bin/env python3
"""
Generate files.json manifest from HTML files in docs/ folder.
Sorts files newest to oldest based on date suffix (MM-DD-YY format).
"""

import json
import re
from pathlib import Path
from datetime import datetime

def extract_date_from_filename(filename):
    """
    Extract date from filename with format: *_MM-DD-YY.html
    Returns a datetime object for sorting, or None if no valid date found.
    """
    # Match date pattern at the end: _MM-DD-YY.html
    pattern = r'_(\d{1,2})-(\d{1,2})-(\d{2})\.html$'
    match = re.search(pattern, filename)
    
    if match:
        month, day, year = match.groups()
        # Convert 2-digit year to 4-digit (assuming 20xx)
        full_year = 2000 + int(year)
        try:
            return datetime(full_year, int(month), int(day))
        except ValueError:
            # Invalid date
            return None
    return None

def main():
    docs_dir = Path('docs')
    
    if not docs_dir.exists():
        print(f"Error: {docs_dir} directory not found")
        return
    
    # Find all HTML files
    html_files = list(docs_dir.glob('*.html'))
    
    # Create list of (date, filename) tuples
    files_with_dates = []
    files_without_dates = []
    
    for file_path in html_files:
        filename = file_path.name
        date = extract_date_from_filename(filename)
        
        if date:
            files_with_dates.append((date, filename))
        else:
            files_without_dates.append(filename)
    
    # Sort by date (newest first)
    files_with_dates.sort(key=lambda x: x[0], reverse=True)
    
    # Combine: dated files first (sorted), then undated files (alphabetically)
    sorted_filenames = [filename for _, filename in files_with_dates]
    sorted_filenames.extend(sorted(files_without_dates))
    
    # Write to files.json
    output_path = docs_dir / 'files.json'
    with open(output_path, 'w', encoding='utf-8') as f:
        json.dump(sorted_filenames, f, indent=2)
    
    print(f"✓ Generated {output_path}")
    print(f"  Total files: {len(sorted_filenames)}")
    print(f"  Dated files: {len(files_with_dates)}")
    print(f"  Undated files: {len(files_without_dates)}")
    
    if sorted_filenames:
        print(f"\nNewest file: {sorted_filenames[0]}")
        if len(sorted_filenames) > 1:
            print(f"Oldest file: {sorted_filenames[-1]}")

if __name__ == '__main__':
    main()