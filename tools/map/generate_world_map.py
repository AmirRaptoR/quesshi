#!/usr/bin/env python3
"""
Builds src/Quesshi.Web/wwwroot/maps/world.svg from a Natural Earth 1:110m
"Admin 0 Countries" GeoJSON export.

See the README.md next to the generated SVG for the exact source, version
and licence. This script only concerns itself with turning that GeoJSON
into a small, ISO-coded, equirectangular SVG -- it does not fetch the data
(fetching once and vetting the result by hand is safer than trusting a
script to re-fetch identical bytes from a mirror months or years later).

Projection: the whole point of this asset is that it is Plate Carree
(equirectangular), not Robinson/Miller/Mercator like most free world SVGs.
That projection is nothing more than a linear map from (lon, lat) to (x, y):

    x = lon
    y = -lat        (SVG y grows downward; latitude grows upward)

with viewBox="-180 -90 360 180". No trigonometry, no ellipsoid, just the
identity on longitude and a sign flip on latitude. Quesshi.Shared's
WorldMapProjection implements the exact same two lines in C# so the server
and the client agree with this file by construction.

Usage:
    python3 generate_world_map.py <path-to-ne_110m_admin_0_countries.geojson> <output-svg-path>
"""
from __future__ import annotations

import json
import re
import sys

ISO2_RE = re.compile(r"^[A-Z]{2}$")

# Two Natural Earth features are disputed territories with no officially
# assigned ISO 3166-1 alpha-2 code (Northern Cyprus, Somaliland). They are
# skipped rather than invented, so "the set of ISO codes in the SVG" stays a
# set of *real* codes a validator can trust.
SKIPPED_NO_ISO = {"N. Cyprus", "Somaliland"}


def resolve_iso(props: dict) -> str | None:
    """
    Natural Earth's plain ISO_A2 column is "-99" for a handful of countries
    whose de-facto and de-jure borders differ (France, Norway, Kosovo) --
    the exact rows this map cares most about getting right. ISO_A2_EH
    ("Extended Handover") fills those in with the code and is preferred;
    ISO_A2 is the fallback for anything ISO_A2_EH itself left blank.
    """
    for key in ("ISO_A2_EH", "ISO_A2"):
        code = (props.get(key) or "").strip().upper()
        if ISO2_RE.match(code):
            return code
    return None


def ring_to_path(ring: list[list[float]]) -> str:
    # lon -> x, lat -> -y (see module docstring). Two decimal places is
    # already finer than the 1:110m simplification these boundaries were
    # drawn at (about 1.1 km per hundredth of a degree at the equator), so
    # extra digits would only inflate the file without adding real detail.
    points = [f"{lon:.2f},{-lat:.2f}" for lon, lat, *_ in ring]
    return "M" + "L".join(points) + "Z"


def geometry_to_path(geometry: dict) -> str:
    kind = geometry["type"]
    if kind == "Polygon":
        rings = geometry["coordinates"]
    elif kind == "MultiPolygon":
        rings = [ring for polygon in geometry["coordinates"] for ring in polygon]
    else:
        raise ValueError(f"Unexpected geometry type for a country: {kind}")
    return "".join(ring_to_path(ring) for ring in rings)


def build_svg(features: list[dict]) -> str:
    by_iso: dict[str, list[dict]] = {}
    skipped: list[str] = []

    for feature in features:
        props = feature["properties"]
        name = props.get("NAME", "?")
        iso = resolve_iso(props)
        if iso is None:
            if name not in SKIPPED_NO_ISO:
                skipped.append(f"{name} ({props.get('ISO_A2')!r})")
            continue
        by_iso.setdefault(iso, []).append(feature)

    if skipped:
        raise ValueError(
            "Unexpected features with no resolvable ISO code (update SKIPPED_NO_ISO "
            f"if these are intentionally excluded): {skipped}"
        )

    parts = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        "<!--",
        "  World map, equirectangular (Plate Carree) projection.",
        "  One <path> per ISO 3166-1 alpha-2 country code, generated from Natural",
        "  Earth 1:110m Admin 0 Countries. See README.md in this folder for the",
        "  exact source, version and licence, and tools/map/generate_world_map.py",
        "  in the repository root for how this file was produced.",
        "",
        "  viewBox is \"-180 -90 360 180\": x is longitude, y is negated latitude,",
        "  both linear. (0,0) is the SVG origin at the exact centre of the box;",
        "  the north pole is the top edge (y=-90) and the south pole the bottom",
        "  edge (y=90). Quesshi.Shared.WorldMapProjection implements the same",
        "  mapping in C# for both the client and the server.",
        "-->",
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="-180 -90 360 180" '
        'role="img" aria-label="World map">',
        # fill-rule="evenodd" on the group means a hole in a country (e.g. the
        # ring Natural Earth cuts into South Africa for Lesotho) renders as a
        # hole regardless of whether GeoJSON's right-hand-rule winding order
        # was preserved through simplification -- evenodd only counts ring
        # crossings, so it does not care which way a ring winds.
        '<g id="countries" fill-rule="evenodd">',
    ]

    for iso in sorted(by_iso):
        group = by_iso[iso]
        name = group[0]["properties"].get("NAME", iso)
        d = "".join(geometry_to_path(f["geometry"]) for f in group)
        safe_name = name.replace("&", "&amp;").replace("<", "&lt;")
        parts.append(f'<path id="{iso}" data-iso="{iso}" class="country" d="{d}"><title>{safe_name}</title></path>')

    parts.append("</g>")
    parts.append("</svg>")
    parts.append("")
    return "\n".join(parts)


def main() -> None:
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(1)

    src_path, out_path = sys.argv[1], sys.argv[2]
    with open(src_path, "r", encoding="utf-8") as f:
        data = json.load(f)

    svg = build_svg(data["features"])

    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(svg)

    path_count = svg.count("<path ")
    print(f"Wrote {out_path}: {path_count} country paths, {len(svg):,} bytes")


if __name__ == "__main__":
    main()
