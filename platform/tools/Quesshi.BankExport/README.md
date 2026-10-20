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

Create the categories first through the platform admin category screen or API, preserving each exported `slug`, names, icon, colour, order and active state. Translate the `triviaCategorySlugs` and `votingCategorySlugs` in `settings.json` to the IDs of those created categories, then apply the resulting allow-lists in bank settings. The rows use `categorySlug`, so the importer resolves them after the categories exist.

The import API accepts the media bundle as a ZIP upload, not as the `media/` directory. From the bundle directory, create the ZIP with its `media/...` entry paths intact:

```sh
zip -r media.zip media
```

For every generated question file, choose its kind in the admin import screen, attach the question JSON as the file and `media.zip` in the **media bundle** field, then run a **dry run**. Resolve every reported failure before submitting the same file and the same ZIP with dry run disabled. For the API, send a `multipart/form-data` `POST /api/admin/import` with `kind`, `dryRun` (`true` for the rehearsal and `false` to import), `file` (the question JSON), and `mediaBundle` (the `media.zip` upload; `bundle` is also accepted). Absolute HTTP(S) media URLs are fetched by the platform; local legacy media is read from the ZIP's `media/...` entries.

`report.json` lists all skipped questions, missing category references, missing media and paths outside the supplied media root. Player-owned questions, player data, sessions, counters, served tokens and reports are never exported.
