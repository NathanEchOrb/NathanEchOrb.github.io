#!/bin/bash
# manage_reports.sh
# Detects mid-week report drops, builds a virtual weekly report from daily diffs,
# and archives the partial report when the real Monday report arrives.
#
# Usage:
#   ./manage_reports.sh              # normal run (moves files, modifies partial report)
#   ./manage_reports.sh --dry-run    # preview what would happen without changing anything
#   ./manage_reports.sh --test       # run against docs/ without needing git staged files

set -euo pipefail

REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null || echo "$(cd "$(dirname "$0")" && pwd)")
DOCS_DIR="$REPO_ROOT/docs"
TEMP_DIR="$REPO_ROOT/temp_doc"
STATE_FILE="$TEMP_DIR/.virtual_state.json"

DRY_RUN=false
TEST_MODE=false

for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=true ;;
    --test)    TEST_MODE=true ;;
  esac
done

log() { echo "[manage_reports] $*"; }

ensure_temp_dir() {
  if [ "$DRY_RUN" = true ]; then
    log "(dry-run) Would create $TEMP_DIR"
  else
    mkdir -p "$TEMP_DIR"
  fi
}

# Parse date components from filename like opportunities_14days_4-6-26_enriched.html
# Returns: MM DD YY
parse_filename_date() {
  local fname="$1"
  local date_field
  date_field=$(echo "$fname" | awk -F'_' '{print $3}')
  local mm dd yy
  mm=$(echo "$date_field" | cut -d'-' -f1)
  dd=$(echo "$date_field" | cut -d'-' -f2)
  yy=$(echo "$date_field" | cut -d'-' -f3)
  echo "$mm $dd $yy"
}

# Get the day of week for a date (0=Sunday, 1=Monday, ... 6=Saturday)
get_day_of_week() {
  local mm=$1 dd=$2 yy=$3
  date -d "20${yy}-$(printf '%02d' "$mm")-$(printf '%02d' "$dd")" +%u 2>/dev/null
  # %u gives 1=Monday ... 7=Sunday
}

# Get next Monday's date from a given date
get_next_monday() {
  local mm=$1 dd=$2 yy=$3
  local full_date="20${yy}-$(printf '%02d' "$mm")-$(printf '%02d' "$dd")"
  local dow
  dow=$(date -d "$full_date" +%u)
  local days_until_monday=$(( (8 - dow) % 7 ))
  if [ "$days_until_monday" -eq 0 ]; then
    days_until_monday=7
  fi
  date -d "$full_date + $days_until_monday days" +"%-m-%-d-%y"
}

# Get the most recent Monday on or before a given date
get_prev_monday() {
  local mm=$1 dd=$2 yy=$3
  local full_date="20${yy}-$(printf '%02d' "$mm")-$(printf '%02d' "$dd")"
  local dow
  dow=$(date -d "$full_date" +%u)
  local days_since_monday=$(( (dow - 1) % 7 ))
  date -d "$full_date - $days_since_monday days" +"%-m-%-d-%y"
}

# Format a M-D-YY string into a comparable YYYYMMDD integer
date_to_sortable() {
  local date_str="$1"
  local mm dd yy
  mm=$(echo "$date_str" | cut -d'-' -f1)
  dd=$(echo "$date_str" | cut -d'-' -f2)
  yy=$(echo "$date_str" | cut -d'-' -f3)
  printf "20%02d%02d%02d" "$yy" "$mm" "$dd"
}

# Extract all SAM.gov opportunity IDs from an HTML file (the unique 32-char hex in URLs)
extract_opportunity_ids() {
  local file="$1"
  grep -oP 'sam\.gov/workspace/contract/opp/\K[a-f0-9]+' "$file" | sort -u
}

# Extract a full table row (<tr>...</tr>) that contains a given opportunity ID
extract_row_by_id() {
  local file="$1"
  local opp_id="$2"
  # Use awk to grab the <tr>...</tr> block containing this ID
  awk -v id="$opp_id" '
    /<tr>/ { row = ""; in_row = 1 }
    in_row { row = row $0 "\n" }
    /<\/tr>/ {
      if (in_row && index(row, id) > 0) {
        printf "%s", row
      }
      in_row = 0
    }
  ' "$file"
}

# Read state file to get current partial report info
read_state() {
  if [ -f "$STATE_FILE" ]; then
    cat "$STATE_FILE"
  else
    echo "{}"
  fi
}

# Write state file
write_state() {
  local virtual_name="$1"
  local last_processed="$2"
  local prev_monday="$3"
  if [ "$DRY_RUN" = true ]; then
    log "(dry-run) Would write state: virtual=$virtual_name, last=$last_processed, week=$prev_monday"
  else
    cat > "$STATE_FILE" << STATEEOF
{
  "virtual_report": "$virtual_name",
  "last_processed_file": "$last_processed",
  "week_of": "$prev_monday"
}
STATEEOF
  fi
}

clear_state() {
  if [ "$DRY_RUN" = true ]; then
    log "(dry-run) Would clear state file"
  else
    rm -f "$STATE_FILE"
  fi
}

# Get the window prefix from a filename (e.g., "14days")
get_window() {
  echo "$1" | awk -F'_' '{print $2}'
}

# Build partial report filename for the next Monday
build_virtual_name() {
  local window="$1"
  local next_monday_date="$2"
  echo "opportunities_${window}_${next_monday_date}_partial.html"
}

# Check if a Monday report (full or partial) exists in docs/ for the week containing a given date
monday_report_exists() {
  local mm=$1 dd=$2 yy=$3
  local prev_mon
  prev_mon=$(get_prev_monday "$mm" "$dd" "$yy")
  local found
  found=$(find "$DOCS_DIR" -maxdepth 1 \( -name "opportunities_*_${prev_mon}_enriched.html" -o -name "opportunities_*_${prev_mon}_partial.html" \) -type f 2>/dev/null | head -1)
  [ -n "$found" ]
}

# Check if a date is within N days of today
is_within_days() {
  local mm=$1 dd=$2 yy=$3 max_days=$4
  local file_epoch today_epoch diff_days
  file_epoch=$(date -d "20${yy}-$(printf '%02d' "$mm")-$(printf '%02d' "$dd")" +%s)
  today_epoch=$(date +%s)
  diff_days=$(( (today_epoch - file_epoch) / 86400 ))
  # Allow negative (future dates) and up to max_days in the past
  [ "$diff_days" -le "$max_days" ]
}

# Collect new HTML files to process.
# In normal mode: only staged files. In test mode: all files in docs/.
get_new_files() {
  if [ "$TEST_MODE" = true ]; then
    find "$DOCS_DIR" -maxdepth 1 -name "opportunities_*_enriched.html" -type f -printf '%f\n' | sort
  else
    git diff --cached --name-only -- 'docs/*.html' 2>/dev/null \
      | grep -v '_partial\.html' \
      | sed 's|^docs/||' \
      | sort
  fi
}

# ─── Main Logic ──────────────────────────────────────────────────────────────

main() {
  log "Starting report management (dry_run=$DRY_RUN, test=$TEST_MODE)"

  # Load current state
  local state
  state=$(read_state)
  local current_virtual
  current_virtual=$(echo "$state" | grep -oP '"virtual_report"\s*:\s*"\K[^"]+' 2>/dev/null || echo "")
  local current_week
  current_week=$(echo "$state" | grep -oP '"week_of"\s*:\s*"\K[^"]+' 2>/dev/null || echo "")
  local last_processed
  last_processed=$(echo "$state" | grep -oP '"last_processed_file"\s*:\s*"\K[^"]+' 2>/dev/null || echo "")

  # In test mode, figure out which files are "new" by comparing against temp_doc contents
  # In normal mode, use git staged files
  local new_files
  new_files=$(get_new_files)

  if [ -z "$new_files" ]; then
    log "No new report files to process."
    return 0
  fi

  # Process each new file
  while IFS= read -r fname; do
    # Skip partial reports
    if [[ "$fname" == *"_partial.html" ]]; then
      continue
    fi

    # Skip files.json or non-opportunity files
    if [[ "$fname" != opportunities_* ]]; then
      continue
    fi

    log "Processing: $fname"

    local date_parts
    read -r mm dd yy <<< "$(parse_filename_date "$fname")"
    local dow
    dow=$(get_day_of_week "$mm" "$dd" "$yy")
    local file_date="${mm}-${dd}-${yy}"
    local window
    window=$(get_window "$fname")

    if [ "$dow" -eq 1 ]; then
      # ── Monday report: this is a real weekly report ──
      log "  Monday report detected ($file_date)"

      # Check if a partial report exists for this same week — replace it
      local partial_for_this_week="opportunities_${window}_${file_date}_partial.html"
      if [ -f "$DOCS_DIR/$partial_for_this_week" ]; then
        log "  Full report replaces partial: removing $partial_for_this_week"
        if [ "$DRY_RUN" = true ]; then
          log "  (dry-run) Would remove $DOCS_DIR/$partial_for_this_week"
        else
          ensure_temp_dir
          mv "$DOCS_DIR/$partial_for_this_week" "$TEMP_DIR/$partial_for_this_week"
          git reset HEAD -- "docs/$partial_for_this_week" 2>/dev/null || true
        fi
      fi

      # Clear state if this Monday report closes out the active partial
      if [ -n "$current_virtual" ] && [ "$current_virtual" = "$partial_for_this_week" ]; then
        clear_state
        current_virtual=""
        current_week=""
        last_processed=""
      fi

      log "  Keeping Monday report in docs/"

    else
      # ── Non-Monday report ──
      log "  Non-Monday report detected (day=$dow, date=$file_date)"

      # Skip reports older than 14 days — not relevant to partial report logic
      if ! is_within_days "$mm" "$dd" "$yy" 14; then
        log "  Older than 2 weeks, skipping."
        continue
      fi

      # If no Monday report exists for this week, treat as a late Monday report
      if ! monday_report_exists "$mm" "$dd" "$yy"; then
        log "  No Monday report found for this week — treating as late Monday report."
        log "  Keeping in docs/"
        continue
      fi

      log "  Monday report already exists for this week — processing as mid-week update."
      ensure_temp_dir

      local next_mon
      next_mon=$(get_next_monday "$mm" "$dd" "$yy")
      local prev_mon
      prev_mon=$(get_prev_monday "$mm" "$dd" "$yy")
      local virtual_name
      virtual_name=$(build_virtual_name "$window" "$next_mon")

      # Recover state if the partial already exists on disk but state was lost
      if [ -f "$DOCS_DIR/$virtual_name" ] && [ "$current_virtual" != "$virtual_name" ]; then
        log "  Found existing partial on disk: $virtual_name (recovering state)"
        current_virtual="$virtual_name"
        current_week="$prev_mon"
      fi

      if [ "$current_virtual" != "$virtual_name" ]; then
        # ── First mid-week report for this upcoming week ──
        log "  First mid-week report for week of $next_mon"
        log "  Creating partial report: $virtual_name"

        if [ "$DRY_RUN" = true ]; then
          log "  (dry-run) Would copy $fname → $virtual_name in docs/"
          log "  (dry-run) Would move $fname → temp_doc/"
        else
          # Copy the file as the partial report foundation
          cp "$DOCS_DIR/$fname" "$DOCS_DIR/$virtual_name"
          # Move original to temp_doc
          mv "$DOCS_DIR/$fname" "$TEMP_DIR/$fname"
          # Stage the partial report, unstage the moved file
          git add "$DOCS_DIR/$virtual_name"
          git reset HEAD -- "docs/$fname" 2>/dev/null || true
        fi

        write_state "$virtual_name" "$fname" "$prev_mon"
        current_virtual="$virtual_name"
        current_week="$prev_mon"
        last_processed="$fname"

      else
        # ── Subsequent mid-week report: diff and append new entries ──
        log "  Subsequent mid-week report, diffing against partial report"

        # Find the most recent file to diff against (the last processed file in temp_doc)
        local prev_file=""
        if [ -n "$last_processed" ] && [ -f "$TEMP_DIR/$last_processed" ]; then
          prev_file="$TEMP_DIR/$last_processed"
        fi

        local new_file="$DOCS_DIR/$fname"
        local virtual_file="$DOCS_DIR/$virtual_name"

        # Get IDs already in the partial report
        local existing_ids
        existing_ids=$(extract_opportunity_ids "$virtual_file")

        # Get IDs in the new file
        local new_ids
        new_ids=$(extract_opportunity_ids "$new_file")

        # Find IDs in new file that are NOT in the partial report
        local added_ids
        added_ids=$(comm -23 <(echo "$new_ids") <(echo "$existing_ids"))

        local count
        count=$(echo "$added_ids" | grep -c '[a-f0-9]' || echo 0)
        log "  Found $count new entries to append"

        if [ "$count" -gt 0 ] && [ "$DRY_RUN" = false ]; then
          # Extract new rows and append them before </tbody>
          local new_rows=""
          while IFS= read -r opp_id; do
            [ -z "$opp_id" ] && continue
            local row
            row=$(extract_row_by_id "$new_file" "$opp_id")
            new_rows="${new_rows}${row}"
          done <<< "$added_ids"

          if [ -n "$new_rows" ]; then
            # Insert new rows before the closing </tbody> tag
            local temp_virtual
            temp_virtual=$(mktemp)
            awk -v rows="$new_rows" '
              /<\/tbody>/ { printf "%s", rows }
              { print }
            ' "$virtual_file" > "$temp_virtual"
            mv "$temp_virtual" "$virtual_file"
            log "  Appended $count new entries to $virtual_name"
          fi
        elif [ "$DRY_RUN" = true ]; then
          log "  (dry-run) Would append $count new entries to $virtual_name"
        fi

        # Move the mid-week file to temp_doc
        if [ "$DRY_RUN" = true ]; then
          log "  (dry-run) Would move $fname → temp_doc/"
        else
          mv "$new_file" "$TEMP_DIR/$fname"
          git add "$DOCS_DIR/$virtual_name"
          git reset HEAD -- "docs/$fname" 2>/dev/null || true
        fi

        write_state "$virtual_name" "$fname" "$current_week"
        last_processed="$fname"
      fi
    fi

  done <<< "$new_files"

  log "Done."
}

main
