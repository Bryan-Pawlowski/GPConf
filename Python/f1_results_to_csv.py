#!/usr/bin/env python3
"""
f1_results_to_csv.py

Scrapes F1 results pages from motorsport.com and saves them as CSV files.

Supported URL pattern:
  https://www.motorsport.com/f1/results/{year}/{race-slug}/?st={SESSION}

Where SESSION is: FP1, FP2, FP3, Q1, Q2, Q3, RACE, SS (sprint qualifying), SPR (sprint)

Usage:
  python f1_results_to_csv.py <url> [<url> ...]
  python f1_results_to_csv.py --file urls.txt

Output filenames are derived from the URL, e.g.:
  2025_abu_dhabi_gp_fp1.csv
  2025_abu_dhabi_gp_q1.csv
  2025_abu_dhabi_gp_race.csv
"""

import sys
import csv
import re
import argparse
from pathlib import Path
from urllib.parse import urlparse, parse_qs

try:
    from playwright.sync_api import sync_playwright
    from bs4 import BeautifulSoup
except ImportError:
    print("Missing dependencies. Run: pip install playwright beautifulsoup4 && python -m playwright install chromium")
    sys.exit(1)


# ── URL / filename helpers ────────────────────────────────────────────────────

def derive_filename(url: str) -> str:
    """
    Build a descriptive CSV filename from a motorsport.com results URL.

    Examples:
      .../2025/abu-dhabi-gp-653231/?st=FP1  -> 2025_abu_dhabi_gp_fp1.csv
      .../2025/abu-dhabi-gp-653231/?st=Q1   -> 2025_abu_dhabi_gp_q1.csv
      .../2025/abu-dhabi-gp-653231/?st=RACE -> 2025_abu_dhabi_gp_race.csv
    """
    parsed = urlparse(url)
    session = parse_qs(parsed.query).get("st", ["unknown"])[0].lower()

    # Path: /f1/results/{year}/{race-slug-id}/
    parts = [p for p in parsed.path.split("/") if p]
    try:
        results_idx = parts.index("results")
        year      = parts[results_idx + 1]
        race_slug = parts[results_idx + 2]
        # Strip trailing numeric event ID (e.g. "abu-dhabi-gp-653231" → "abu-dhabi-gp")
        race_slug = re.sub(r"-\d+$", "", race_slug)
    except (ValueError, IndexError):
        race_slug = "unknown"
        year = "unknown"

    safe_slug    = race_slug.replace("-", "_")
    safe_session = session.replace("-", "_")
    return f"{year}_{safe_slug}_{safe_session}.csv"


def slug_to_name(slug: str) -> str:
    """Convert a driver URL slug to a display name.  'lando-norris' -> 'Lando Norris'"""
    return " ".join(word.capitalize() for word in slug.split("-"))


# ── Page fetching ─────────────────────────────────────────────────────────────

def fetch_page(url: str) -> BeautifulSoup:
    with sync_playwright() as p:
        browser = p.chromium.launch(
            args=["--disable-blink-features=AutomationControlled"]
        )
        ctx = browser.new_context(
            user_agent=(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/124.0.0.0 Safari/537.36"
            ),
            viewport={"width": 1280, "height": 900},
            locale="en-US",
        )
        page = ctx.new_page()
        page.add_init_script(
            "Object.defineProperty(navigator, 'webdriver', {get: () => undefined})"
        )
        page.goto(url, wait_until="domcontentloaded", timeout=30000)
        page.wait_for_timeout(4000)
        html = page.content()
        browser.close()
    return BeautifulSoup(html, "html.parser")


# ── Cell extraction helpers ───────────────────────────────────────────────────

def cell_text(td) -> str:
    """Return the plain text of a cell, stripping whitespace."""
    return td.get_text(separator=" ").strip()


def get_cell(row, field_class: str):
    """Return the <td> whose class list contains the given field class, or None."""
    return row.find("td", class_=lambda c: c and field_class in c.split())


def extract_driver(td) -> tuple[str, str]:
    """
    Return (full_name, team) from a driver cell.

    The href '/driver/lando-norris/289316/' is used as the authoritative name
    source since the visible text is only an abbreviation ('L. Norris').
    """
    a = td.find("a", class_="ms-link")
    if not a:
        return cell_text(td), ""

    href = a.get("href", "")
    # Extract driver slug from /driver/{slug}/{id}/
    m = re.search(r"/driver/([^/]+)/", href)
    name = slug_to_name(m.group(1)) if m else cell_text(td)

    team_span = a.find("span", class_="team")
    team = team_span.get_text(strip=True) if team_span else ""

    return name, team


def extract_time_cells(td) -> tuple[str, str]:
    """
    The time cell contains one or two <p> elements:
      - 1 <p>  → leader; value is the absolute time.
      - 2 <p>s → non-leader; first is gap (+X.XXX), second is absolute time.

    Returns (gap, time) as strings.
    """
    ps = [p.get_text(strip=True) for p in td.find_all("p") if p.get_text(strip=True)]
    if len(ps) == 0:
        return "", ""
    if len(ps) == 1:
        return "", ps[0]          # leader: no gap
    return ps[0], ps[1]           # gap, absolute time


def row_value(td) -> str:
    """Extract text from a standard ms-table_row-value cell."""
    span = td.find(class_="ms-table_row-value") if td else None
    return span.get_text(strip=True) if span else ""


# ── Table parsing ─────────────────────────────────────────────────────────────

def parse_table(soup: BeautifulSoup, session: str) -> tuple[list[str], list[list[str]]]:
    table = soup.find("table")
    if not table:
        raise ValueError("No <table> found on the page — the page may not have loaded correctly.")

    tbody = table.find("tbody")
    if not tbody:
        raise ValueError("Table has no <tbody>.")

    session_upper = session.upper()
    is_race = session_upper in ("RACE", "SPR")      # full race or sprint race
    is_quali = session_upper in ("Q1", "Q2", "Q3", "SS")  # any qualifying segment

    if is_race:
        headers = ["Position", "Driver", "Team", "Car No.", "Laps",
                   "Time", "Gap", "km/h", "Pits", "Points", "Retirement"]
    elif is_quali:
        headers = ["Position", "Driver", "Team", "Car No.", "Laps",
                   "Time", "Gap", "Tyres", "km/h"]
    else:
        # Practice
        headers = ["Position", "Driver", "Team", "Car No.", "Laps",
                   "Time", "Gap", "Tyres", "km/h"]

    rows: list[list[str]] = []
    for tr in tbody.find_all("tr", class_="ms-table_row"):
        pos_td    = get_cell(tr, "ms-table_field--pos")
        driver_td = get_cell(tr, "ms-table_field--result_driver_id")
        num_td    = get_cell(tr, "ms-table_field--number")
        laps_td   = get_cell(tr, "ms-table_field--laps")
        time_td   = get_cell(tr, "ms-table_field--time")
        gap_td    = get_cell(tr, "ms-table_field--interval")
        tyres_td  = get_cell(tr, "ms-table_field--best_tyres")
        speed_td  = get_cell(tr, "ms-table_field--avg_speed")
        pts_td    = get_cell(tr, "ms-table_field--points")
        pits_td   = get_cell(tr, "ms-table_field--pits")
        ret_td    = get_cell(tr, "ms-table_field--retirement")

        pos    = row_value(pos_td)
        number = row_value(num_td)
        laps   = row_value(laps_td)
        gap    = row_value(gap_td)
        tyres  = row_value(tyres_td)
        speed  = row_value(speed_td)
        points = row_value(pts_td)
        pits   = row_value(pits_td)
        ret    = row_value(ret_td)

        name, team = extract_driver(driver_td) if driver_td else ("", "")
        _, time = extract_time_cells(time_td) if time_td else ("", "")

        if is_race:
            row = [pos, name, team, number, laps, time, gap, speed, pits, points, ret]
        else:
            row = [pos, name, team, number, laps, time, gap, tyres, speed]

        rows.append(row)

    return headers, rows


# ── CSV output ────────────────────────────────────────────────────────────────

def save_csv(filename: str, headers: list[str], rows: list[list[str]]) -> Path:
    out_path = Path(filename)
    with out_path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(headers)
        writer.writerows(rows)
    return out_path


# ── Main ──────────────────────────────────────────────────────────────────────

def process_url(url: str, out_dir: Path = Path(".")) -> None:
    session = parse_qs(urlparse(url).query).get("st", ["?"])[0]
    print(f"Fetching [{session}]: {url}")
    try:
        soup     = fetch_page(url)
        headers, rows = parse_table(soup, session)
        filename = out_dir / derive_filename(url)
        path     = save_csv(str(filename), headers, rows)
        print(f"  -> Saved {len(rows)} rows to '{path}'")
    except Exception as e:
        print(f"  ERROR: {e}")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Scrape F1 results from motorsport.com and save as CSV."
    )
    parser.add_argument(
        "urls", nargs="*", metavar="URL",
        help="One or more motorsport.com results URLs (with ?st= parameter)",
    )
    parser.add_argument(
        "--file", "-f", metavar="FILE",
        help="Text file containing one URL per line",
    )
    parser.add_argument(
        "--output", "-o", metavar="DIR", default=".",
        help="Destination folder for CSV files (default: current directory)",
    )
    args = parser.parse_args()

    urls: list[str] = list(args.urls)
    if args.file:
        with open(args.file, encoding="utf-8") as f:
            urls += [l.strip() for l in f if l.strip() and not l.startswith("#")]

    if not urls:
        parser.print_help()
        sys.exit(1)

    out_dir = Path(args.output)
    out_dir.mkdir(parents=True, exist_ok=True)

    for url in urls:
        process_url(url, out_dir)


if __name__ == "__main__":
    main()
