# Legacy bank export

This one-off tool reads one legacy tenant MongoDB database without modifying it and produces a platform import bundle. It is intentionally outside the deployed platform products.

Restore a MongoDB dump locally, then run:

```sh
dotnet run --project platform/tools/Quesshi.BankExport -- \
  --connection 'mongodb://127.0.0.1:27017' \
  --database legacy_tenant \
  --output ./bank-bundle \
  --media-root /path/to/legacy/wwwroot/media
```

The empty output directory receives `categories.json`, `settings.json`, one JSON file (or numbered files when an import limit requires it) for each populated kind, `media/`, and `report.json`.

Create the categories first through the platform admin category screen or API, preserving each exported `slug`, names, icon, colour, order and active state. Translate the `triviaCategorySlugs` and `votingCategorySlugs` in `settings.json` to the IDs of those created categories, then apply the resulting allow-lists in bank settings. For each generated question file, first select its kind and run an import **dry run** with `media/` as the media bundle; resolve every report failure. Then import that same file with the matching bundle. The rows use `categorySlug`, so the importer resolves them after the categories exist. Absolute HTTP(S) media URLs are fetched by the platform; local legacy media is already in `media/`.

`report.json` lists all skipped questions, missing category references, missing media and paths outside the supplied media root. Player-owned questions, player data, sessions, counters, served tokens and reports are never exported.
