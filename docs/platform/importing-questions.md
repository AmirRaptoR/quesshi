# Importing tenant questions

Editors upload a multipart `file` to `POST /api/admin/import`.  The form has a
`kind` (`choice`, `order`, `map_pick`, `poll_choice`, or `poll_roster`), an optional
`format` (`csv` or `json`), optional `mediaBundle` zip, `dryRun`, and default
`status`.  `dryRun` defaults to `true`; status defaults to `pending`; a valid row
`status` overrides the request default.  The route is tenant-scoped, requires editor
access and antiforgery, and committed questions have source `import`.

The compatible legacy names are `choice` → `choice`, `sort` → `order`, `map` →
`map_pick`, `answerSource=fixed` → `poll_choice`, and `answerSource=participants` →
`poll_roster`.  CSV headers and JSON property names are case-insensitive.  Common
fields are `language` (also legacy `lang`), `category` (also `categoryId`), `level`,
`prompt`, `explanation`, `subject`, `aspect`, `status`, `mediaUrl`, `mediaKind`, and
optional `mediaAttribution`; the selected kind supplies its own fields.

The response has `rows`, `accepted`, `rejected`, and nonblocking `warnings`.  Each row
is `would_import`, `imported`, `duplicate`, `invalid`, `bad_row`, `media_unreachable`,
or `media_bundle_invalid`; duplicates name either `existingQuestionId` or `existingRow`.
Request-wide input failures return `{ "error": "..." }` before writes.  Dry runs perform
the same validation, duplicate and bounded URL-header checks as a stable commit, but do
not create questions or media assets and do not download URL response bodies.

An HTTP(S) `mediaUrl` is checked and stored as a tenant asset on commit, retaining
`mediaAttribution`.  A relative media value is only a slash-separated regular file path
inside that request's `mediaBundle`; absolute filesystem paths, backslashes, empty
segments and `.`/`..` traversal are rejected.  Use
`GET /api/admin/import/templates/{kind}.{csv|json}` to download an editor-only example.
