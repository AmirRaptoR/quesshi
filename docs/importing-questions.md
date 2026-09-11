# Importing questions from CSV / JSON

`/admin/questions` can bulk-import a file of questions instead of writing them one at a time. A file
describes rows of **one `QuestionKind`** — `choice`, `sort` or `map`, chosen when the file is
uploaded — because the columns a row needs differ by kind, and mixing them would mean columns most
rows leave blank. Download the matching template first
(`GET /api/admin/questions/import/template?kind=<kind>&format=<csv|json>`) rather than typing a file
by hand.

Every row is bound and validated through the exact same path the single-question admin form uses
(`QuestionSaveBinding` → `Question.Create`), so a row that would be refused by the form comes back
with the same error code. A dry run (`dryRun=true`, the default) reports what would be accepted and
rejected without writing anything; committing (`dryRun=false`) re-runs the same checks against
current data and writes exactly the rows that still pass.

## Common columns, every kind

| Column | Required | Notes |
|---|---|---|
| `lang` | yes | exactly `fa`, `en` or `nl` |
| `categoryId` | yes | not checked against the category list — same as the single-question form |
| `level` | yes | `1`–`5` |
| `prompt` | yes | |
| `explanation` | no | |
| `mediaUrl` | no | a URL, not an upload — see [Media](#media) |
| `mediaKind` | no | `image`, `audio` or `video`; ignored unless `mediaUrl` is set, defaults to `image` |
| `status` | no | `pending` (default), `approved` or `rejected` |
| `subject` / `aspect` | no | together form the dedup key — see [Deduplication](#deduplication) |

## Choice

| Column | Notes |
|---|---|
| `choice1`..`choice4` | four distinct, non-blank options |
| `correctIndex` | 0-based index of the right one |

## Sort

| Column | Notes |
|---|---|
| `item1`..`item4` | four distinct, non-blank items, **already in their correct order** |

There is no `correctIndex` column: the domain stores a sort's items in the order they are correct,
and shuffles only when serving a round (`SortOrder`). Writing them out of order writes a wrong
answer, not a hint about scrambling — the importer does not reorder anything.

## Map

| Column | Notes |
|---|---|
| `targetShape` | `country` or `city` |
| `countryCode` | ISO 3166-1 alpha-2, for a `country` target — must be a country the bundled map can draw |
| `latitude` / `longitude` / `radiusKm` | for a `city` target; radius is 10–2000 km |
| `baseLayer` | `blank` or `borders` |

A column irrelevant to the row's `targetShape` (`latitude` on a country row, `countryCode` on a city
row) is ignored, not rejected — the template's header carries all five map fields regardless of which
shape a given row uses.

## Media

`mediaUrl` is a URL, exactly like the single-question form's media field — there is no
upload-during-import. To attach a file, upload it first through the admin panel's existing media
upload (`POST /api/admin/media`) to get a URL, then put that URL in the row.

## Deduplication

A row supplying **both** `subject` and `aspect` gets a `TopicKey` — the same key the OpenRouter
generator uses to avoid asking for a question twice. That topic is checked against every question
already stored in the same language and against every row already accepted earlier in the same file;
a collision with either is rejected as `duplicate_topic`. A row invalid for another reason never
reserves its topic, so a later row with the same `subject`/`aspect` is still eligible. A row supplying
only one of the two, or neither, is never dedup-rejected, exactly like a hand-authored or generated
question with no topic at all.

## Format notes

- **CSV**: one header row naming the columns above (any order, case-insensitive; unknown extra
  columns are ignored), then one row per question. Quote a field that contains a comma, a quote or a
  newline, doubling any quote inside it. A row with the wrong number of columns is reported as
  `bad_row` without affecting the rest of the file.
- **JSON**: a bare array of objects, keys matching the columns above (case-insensitive). An array
  element that isn't an object is reported as `bad_row`; a document that isn't valid JSON at all fails
  the whole request with one `bad_json` error, since there are no rows yet to report.
- A file over 20 MB or 2,000 rows is refused before anything is parsed.
