#!/usr/bin/env python3
"""
Enrich localities geojson + DB with province (ADM1) names via spatial join.

For each LATAM country:
  1. Download ADM1 polygons from GeoBoundaries.
  2. For each ADM2 locality centroid, find which ADM1 polygon contains it.
  3. Write adm1Name + adm1Gid into the per-country geojson files.
  4. POST updated records to the backend import API.

Usage:
  python3 scripts/localities/enrich-adm1.py [--api-url URL] [--token TOKEN]
"""

import json, sys, os, time, argparse, urllib.request
from pathlib import Path
from shapely.geometry import shape, Point

ROOT = Path(__file__).resolve().parents[2]
DATA_DIR = ROOT / "frontend" / "public" / "data"

COUNTRIES = [
    ("ARG", "ar"), ("BRA", "br"), ("CHL", "cl"), ("URY", "uy"),
    ("PRY", "py"), ("BOL", "bo"), ("PER", "pe"), ("ECU", "ec"),
    ("COL", "co"), ("VEN", "ve"), ("MEX", "mx"), ("CRI", "cr"),
    ("PAN", "pa"), ("GTM", "gt"), ("HND", "hn"), ("SLV", "sv"),
    ("NIC", "ni"), ("DOM", "do"),
]


def fetch_json(url: str):
    for attempt in range(3):
        try:
            with urllib.request.urlopen(url, timeout=30) as r:
                return json.loads(r.read())
        except Exception as e:
            if attempt == 2:
                raise
            print(f"  retry {attempt+1}: {e}")
            time.sleep(2)


def get_adm1_url(iso3: str) -> str:
    meta = fetch_json(f"https://www.geoboundaries.org/api/current/gbOpen/{iso3}/ADM1/")
    return meta.get("simplifiedGeometryGeoJSON") or meta.get("gjDownloadURL", "")


def build_adm1_index(iso3: str):
    """Returns list of (shapely_polygon, adm1_name, adm1_gid)."""
    print(f"  Downloading ADM1 for {iso3}...", end=" ", flush=True)
    url = get_adm1_url(iso3)
    gj = fetch_json(url)
    index = []
    for f in gj["features"]:
        try:
            geom = shape(f["geometry"])
            name = f["properties"].get("shapeName", "")
            gid = f["properties"].get("shapeID", "")
            index.append((geom, name, gid))
        except Exception:
            pass
    print(f"{len(index)} provinces")
    return index


def find_adm1(index, lat: float, lng: float):
    """Point-in-polygon lookup. Returns (adm1_name, adm1_gid) or ('', '')."""
    pt = Point(lng, lat)
    for geom, name, gid in index:
        if geom.contains(pt):
            return name, gid
    # fallback: nearest centroid (for points on borders)
    best = min(index, key=lambda x: x[0].centroid.distance(pt), default=None)
    return (best[1], best[2]) if best else ("", "")


def enrich_country(iso3: str, cc: str, adm1_index, dry_run: bool) -> list:
    """Enrich the geojson file and return import items."""
    geojson_path = DATA_DIR / f"localities-{cc}.geojson"
    if not geojson_path.exists():
        print(f"  No geojson found at {geojson_path}, skipping.")
        return []

    with open(geojson_path) as f:
        gj = json.load(f)

    import_items = []
    updated = 0
    for feat in gj["features"]:
        props = feat.get("properties", {})
        gid2 = props.get("gid2", "")
        if not gid2:
            continue

        # Compute centroid from geometry
        try:
            geom = shape(feat["geometry"])
            c = geom.centroid
            lat, lng = c.y, c.x
        except Exception:
            lat, lng = 0.0, 0.0

        adm1_name, adm1_gid = find_adm1(adm1_index, lat, lng)
        if props.get("adm1Name") != adm1_name:
            props["adm1Name"] = adm1_name
            updated += 1

        import_items.append({
            "gid2": gid2,
            "name": props.get("name", ""),
            "adminLevel1Gid": adm1_gid,
            "adminLevel1Name": adm1_name,
            "countryCode": props.get("countryCode", cc.upper()),
            "countryName": props.get("countryName", ""),
            "centroidLat": lat,
            "centroidLng": lng,
        })

    if not dry_run:
        with open(geojson_path, "w") as f:
            json.dump(gj, f, ensure_ascii=False, separators=(",", ":"))
        print(f"  Updated {updated}/{len(gj['features'])} features in {geojson_path.name}")

    return import_items


def post_import(api_url: str, token: str, items: list):
    """POST items to /api/admin/localities/import in batches of 500."""
    batch_size = 500
    total_inserted = 0
    total_updated = 0
    for i in range(0, len(items), batch_size):
        batch = items[i:i+batch_size]
        payload = json.dumps({"items": batch}).encode()
        req = urllib.request.Request(
            f"{api_url}/admin/localities/import",
            data=payload,
            headers={"Content-Type": "application/json", "Authorization": f"Bearer {token}"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=30) as r:
            result = json.loads(r.read())
            total_inserted += result.get("inserted", 0)
            total_updated += result.get("updated", 0)
    print(f"  DB: {total_inserted} inserted, {total_updated} updated")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--api-url", default="https://api.sales.efcloud.tech/api",
                        help="Backend API base URL")
    parser.add_argument("--token", help="Admin JWT token (or set SALESHUB_TOKEN env var)")
    parser.add_argument("--dry-run", action="store_true", help="Skip writing files and API call")
    parser.add_argument("--country", help="Only process this ISO2 country code (e.g. ar)")
    args = parser.parse_args()

    token = args.token or os.environ.get("SALESHUB_TOKEN", "")
    if not token and not args.dry_run:
        print("ERROR: pass --token or set SALESHUB_TOKEN env var")
        sys.exit(1)

    countries = COUNTRIES
    if args.country:
        countries = [(iso3, cc) for iso3, cc in COUNTRIES if cc == args.country.lower()]
        if not countries:
            print(f"Unknown country: {args.country}")
            sys.exit(1)

    all_items = []
    for iso3, cc in countries:
        print(f"\n[{iso3}/{cc.upper()}]")
        try:
            adm1_index = build_adm1_index(iso3)
            items = enrich_country(iso3, cc, adm1_index, args.dry_run)
            all_items.extend(items)
            if items and not args.dry_run:
                print(f"  Posting {len(items)} items to API...")
                post_import(args.api_url, token, items)
        except Exception as e:
            print(f"  FAILED: {e}")

    print(f"\nDone. {len(all_items)} total localities processed.")


if __name__ == "__main__":
    main()
