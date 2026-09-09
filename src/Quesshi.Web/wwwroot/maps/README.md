# world.svg

A world map, one `<path>` per country, for the `Map` question kind
(`docs/sorting-and-map-questions.md`). Ships as a static asset so the game keeps working
offline in the PWA — no mapping library, no tiles, no network request.

## Source and licence

- **Data**: [Natural Earth](https://www.naturalearthdata.com/) 1:110m Cultural Vectors,
  *Admin 0 – Countries*, version 5.1.1.
- **Retrieved from**: the official companion GeoJSON export,
  <https://github.com/nvkelso/natural-earth-vector/blob/master/geojson/ne_110m_admin_0_countries.geojson>,
  commit `9380cca8` (2022-05-13).
- **Licence**: Natural Earth data is [public domain](https://www.naturalearthdata.com/about/terms-of-use/).
  *"You may use the maps in any manner, including modifying the content and design, copyright
  and selling the maps. No permission is needed to use Natural Earth. Crediting the authors
  is unnecessary."* Credited here anyway, for provenance.
- Chosen over Wikimedia/random-repository SVGs specifically because its licence and lineage
  are unambiguous and it is explicitly built for this scale (a phone screen, not a wall poster).

## Why this file and not some other free world SVG

Most freely available world SVGs (Wikipedia's included) use the Robinson, Miller or Mercator
projection, because those look better framed on a page. Any of them would make a city question
silently wrong by hundreds of kilometres while looking perfectly reasonable — nothing about a
Mercator map *looks* wrong until you check a coordinate against it. This file is
**equirectangular (Plate Carrée)** instead: `viewBox="-180 -90 360 180"`, and going from
longitude/latitude to an `x, y` in that box is the identity function with a sign flip
(`x = lon`, `y = -lat`) — see `Quesshi.Shared.WorldMapProjection`, which implements exactly
that and nothing more, on both the client and the server.

## Regenerating it

`tools/map/generate_world_map.py` builds this file from the Natural Earth GeoJSON above:

```
python3 tools/map/generate_world_map.py ne_110m_admin_0_countries.geojson \
    src/Quesshi.Web/wwwroot/maps/world.svg
```

The GeoJSON itself isn't committed (it's ~800 KB of source data the SVG is derived from, not
something the build needs) — fetch it fresh from the URL above if you need to regenerate.

The script:

- Projects every ring with `x = lon, y = -lat` (no other projection is ever applied).
- Prefers Natural Earth's `ISO_A2_EH` column over the plain `ISO_A2` one, because `ISO_A2` is
  `"-99"` for a few countries whose de-facto and de-jure borders differ — France, Norway and
  Kosovo among them — which are exactly the ones a quiz should get right.
- Skips two disputed territories Natural Earth carries with no officially assigned ISO
  3166-1 alpha-2 code (Northern Cyprus, Somaliland), rather than inventing one.
- Emits one `<path id="XX" data-iso="XX">` per resulting code (175 of them), grouped under
  `fill-rule="evenodd"` so holes (Lesotho's cut-out of South Africa, for one) render correctly
  regardless of a ring's winding direction.

## Consuming the ISO code set

`Quesshi.Shared.WorldMapCountries.Codes` is **not** a hand-maintained list — it is parsed at
class-load time from this exact file (embedded into `Quesshi.Shared` at build time via the
`EmbeddedResource` item in its `.csproj`, which points straight at this path rather than a
copy). Add or remove a country here and the set the domain validator checks country targets
against changes with it; there is only one place to edit.
