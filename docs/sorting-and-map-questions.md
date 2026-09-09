# Sorting and map questions

## Context

Every question in Quesshi is the same shape. `Question` holds exactly
`MatchRules.ChoicesPerQuestion` (4) options with a single `CorrectIndex`, an answer is one
`int ChoiceIndex`, and `Scoring.Score` takes a `bool correct`. That form runs from the generator,
through `QuestionSetBuilder`, into both duel state machines, and out to both play screens.

This adds two more forms without disturbing it:

- **Sorting** — put four items in the right order by a stated, measurable criterion.
- **Map** — find a place on a world map, either a country or a city.

A third idea, a turn-based question where players alternate picks from a shared pool of valid
answers, was discussed and deliberately deferred. It is not a question type at all — it changes who
may answer and when — and it is recorded under [Out of scope](#out-of-scope) with the reasons.

## What already exists

- **The redaction discipline.** `LiveRoundCardDto` and `QuestionCardDto` both carry the prompt and
  the choices and never the correct answer. Every new type has to keep that property rather than
  weaken it.
- **How a question becomes playable.** Questions are written by the OpenRouter pipeline
  (`TopUpQuestionBank`) or by hand in the admin panel. Generated ones do **not** wait for review:
  `TopUpOptions.AutoApprove` defaults to `true`, so a top-up publishes straight to players. Review is
  the admin panel's `Approve`/`Reject`, and `ReportsBeforeSuppressed = 3` is what retires a question
  players flag — dropping an approved one back to `Pending`.
- **Deduplication.** `TopicKey.From(subject, aspect)` plus a unique `Lang + Topic` index stops the
  same question being written twice in one language.
- **Selection.** `QuestionSetBuilder.BuildAsync` picks by language, category and difficulty and
  throws `NotEnoughQuestionsException` when nothing fits. Its promise is narrower than it looks: a
  *named* category that has no questions is honoured to the point of failure, but a category id that
  does not exist at all falls back to randomly chosen active ones.

## Decisions

- Two new kinds, `Sort` and `Map`, on the existing `Question`. No new aggregate, no new collection.
- **All-or-nothing scoring.** A sort is right or wrong; a map answer is inside the target or it is
  not. `bool Correct` survives untouched, and with it `PlayerStats`, the per-category accuracy
  records, `TimesServed`/`TimesCorrect`, the leaderboard and the meaning of the speed bonus.
- **Mixed into ordinary duels.** A ten-question duel might hold seven multiple-choice, two sorts and
  one map. No new lobby setting.
- **Authored by the generator and the admin panel**, through the same pipeline and the same review.
- The map is an inline **SVG in an equirectangular projection**. No mapping library, no tiles, no
  network, so it still works offline in the PWA and needs nothing from the CSP.
- Map base layers ship as **blank** and **borders**. A labelled layer needs a localised country-name
  dataset and is deferred.

## 1. The question model

`Question` gains `QuestionKind { Choice, Sort, Map }`, defaulting to `Choice`. Every existing row is
already a valid `Choice` question, so there is no migration and no backfill: the discriminator's
default *is* the existing data. This is the whole reason for the discriminator over separate
aggregates — `IQuestionRepository`, the unique `Lang + Topic` index, `TopicKey`, the generator and
the admin panel all assume one collection and one shape, and forking them would cost far more than
one enum.

**Sorting reuses `Choices`.** The four items are stored in their correct order and the card serves
them shuffled, so no new field holds the answer. The item count stays at
`MatchRules.ChoicesPerQuestion`, which keeps the 20-second clock honest and reuses the bound the
generator and admin already validate against.

The shuffle is seeded by `(matchId, slot)`, not by question id: everyone in a live duel must see the
same arrangement, and seeding by question alone would hand a returning player the order they saw last
time.

**That seed decides where correctness is computed, so it needs stating exactly.** The card cannot
reveal which stored index each served item came from — the stored order *is* the answer. So the
player submits **served positions**, and the server has to invert the shuffle before comparing. The
seed is reconstructible only where the match is known, which is the grain: `LiveMatchGrain` already
computes correctness there (`var correct = question.IsCorrect(choiceIndex)`), and it holds the match
id and the slot. So the grain derives the served order and passes it to the domain, and `Question`
gains a checker of the form "given this served order, is this submission correct" rather than one
that silently assumes an identity mapping.

**Map adds a nullable target and a base layer**, and leaves `Choices` empty. The target is one of two
shapes, so it is a small owned record rather than loose columns: a country carries an ISO 3166-1
alpha-2 code, a city carries a latitude, a longitude and a tolerance radius in kilometres. Only one
of the two is ever populated, and which one it is decides how an answer is checked. The base layer is
`Blank` or `Borders`.

**Validation becomes per-kind, and has to be spelled out** — `Question`'s current guard requires
exactly four distinct choices and an in-range `CorrectIndex`, which is wrong for two of the three
kinds:

| | `Choice` | `Sort` | `Map` |
|---|---|---|---|
| `Choices` | 4, distinct | 4, distinct, in correct order | must be empty |
| `CorrectIndex` | in range | unused, must be 0 | unused, must be 0 |
| Target | — | — | required, exactly one shape |
| Base layer | — | — | required |

A country target is rejected unless its code exists in the bundled SVG, so a question can never be
authored against a country the map cannot draw. A city target needs latitude in −90..90, longitude in
−180..180, and a positive radius; the radius is also bounded above, since a large enough one makes
every answer correct.

**The submitted answer gains one nullable string** beside `ChoiceIndex`, on both `AnswerRecord` and
`LiveAnswer`: `"2,0,3,1"` for a sort, `"DE"` or `"52.37,4.90"` for a map. `Correct` stays a `bool`.

Be precise about what that does and does not cost, because "additive" is only true of *storage*:

- **Stored answers are untouched.** Answers live in the Orleans grain state in Redis
  (`MatchSnapshot`, `LiveRoundSnapshot`); the Mongo archive keeps participant results and question
  ids, not individual answers. An old snapshot deserialises with the new field null and reads as the
  `Choice` answer it always was.
- **The submission path is not additive and has to change.** `AnswerDto` and `LiveHub.Answer` accept
  only an `int`, `LiveMatch.Answer` and `Match.SubmitAnswer` take `int choiceIndex`, and
  `LiveMatch.cs:340` range-checks it against `MatchRules.ChoicesPerQuestion`. All of those gain the
  optional response string, and the range check becomes a `Choice`-only rule — a `Sort` or `Map`
  answer arrives with `ChoiceIndex` unset and must not be rejected for it.
- **`Question.IsCorrect(int)` is not enough.** It is `choiceIndex == CorrectIndex`. It grows a
  per-kind sibling that takes the response string, and for `Sort` also the served order, as above.

**Cards stay redacted, and gain a kind.** `QuestionCardDto` and `LiveRoundCardDto` both grow a
`Kind`, because without it a client has no way to choose a renderer — today the shape is implied and
that stops being true here. A sort card carries the shuffled items; a map card carries the prompt and
the base layer. The correct order and the target are revealed only at reveal, which is the rule the
two card DTOs already follow, extended rather than loosened.

**The reveal contracts change too.** `AnswerResultDto` and `LiveRoundRevealDto` both carry a bare
`int CorrectIndex`, which cannot express a sort order or a map target. Each gains the kind and a
kind-appropriate answer field, and `CorrectIndex` keeps its meaning for `Choice` alone.

## 2. Play and scoring

**Sorting is a drag list with an explicit handle.** The row itself stays inert so a scroll gesture
on a phone cannot reorder by accident. The handle is focusable and reorders by arrow key as well as
by pointer, so the drag is not the only path to an answer. In Persian the handle sits on the row's
start edge, which is the right-hand side — it must be laid out logically rather than hard-coded to
the left.

**The map is tap-to-select, with the name confirming the choice.** Tapping selects a country or
drops a point and shows what was hit in a bar beneath the map; tapping again moves it. That naming
step is what makes a world map usable on a phone without pan and zoom: a player finds out they hit
Belgium instead of the Netherlands before the buzzer rather than after. Pan and zoom are deliberately
not built — small countries are an authoring constraint until they prove to be a real problem.

Because the projection is equirectangular, converting a tap position to latitude and longitude is
plain arithmetic in both directions. A city answer is correct when it falls within the question's
tolerance radius, which doubles as its difficulty lever: 50 km is hard, 300 km is gentle.

**Scoring is unchanged.** `Scoring.Score(correct, taken, limit, level)` already takes a boolean and a
duration, which is exactly what both new kinds produce. The speed bonus, the difficulty weighting and
the zero-for-wrong rule all carry over with no new code.

**Reveal shows the gap.** A sort reveals the correct order beside the submitted one; a map highlights
the target and the player's pick together. That is the job the existing reveal already does when it
colours the right choice and marks the player's.

**The clock stays uniform, for now.** `MatchRules.QuestionTime` is 20 seconds for every question and
is also the divisor in the speed-bonus decay. Four drags is plausible in 20 seconds but tighter than
one tap. A per-kind clock is possible — the live machine already sets `PhaseEndsAt` per round — but
the scoring limit would have to vary in step, and a constant is easier to tune after a few real duels
than a variable is to remove.

## 3. Authoring

**The generator gains a prompt per kind**, feeding the same pipeline. `TopicKey` and the unique
`Lang + Topic` index are unchanged.

**Its inventory has to learn about kinds, or the new ones never get written.** `BucketCount` is
`(Lang, CategoryId, Level, Approved, Pending)` and the top-up fills each bucket to a target. With
3067 existing `Choice` questions every bucket already looks full, so a top-up would conclude there is
nothing to do and generate no sorts and no maps at all. `Kind` joins the bucket key, and each kind
gets its own target — a much smaller one for the new kinds than for `Choice`.

**Generated questions publish immediately.** `AutoApprove` defaults to `true`, so a wrong sort order
or a mislocated city goes straight to players. That is tolerable for multiple choice, where an error
is obvious on sight; it is less so for an ordering nobody can verify at a glance. I would run the
first top-up of each new kind with `AutoApprove` off and review by hand before trusting it.

Each new kind gets one hard constraint, because each can fail in a way multiple-choice cannot:

- **A sort must name an objective criterion.** "Order these by population" is verifiable; "order
  these by beauty" stores an opinion and then tells every player who disagrees that they are wrong.
  The prompt demands a measurable axis, and the criterion is shown to the player as part of the
  question.
- **A map answer is checked against the map.** A country target must resolve to a path in the SVG. A
  city target is the riskier one, since a model will confidently produce coordinates that are off by
  a country, so the generator is asked for the city *and* its country and the pipeline verifies the
  coordinates fall inside that country's polygon before the question is stored.

**The admin panel gets type-aware forms.** Sorting is the item list in its correct order, with the
shuffle happening at serve time so the author always sees the truth. A map target is set by clicking
the map, reusing the play component — so the authoring tool and the game agree by construction rather
than by two implementations being kept in step. The base layer and, for a city, the tolerance radius
are set here too.

Existing questions are untouched: they are `Choice`, and the current form keeps working.

## Out of scope

- **Turn-based shared-pool questions.** One question with several valid answers, players alternating
  picks. Deferred deliberately: it is not a question type but a change to who may answer and when.
  `LiveMatch` opens one round for every active participant at once and `LiveRound.Answers` is a
  dictionary of exactly one answer per player, whereas this needs one player acting at a time and
  several picks from the same player in a round — so that round becomes an ordered sequence and the
  phase clock becomes a per-turn clock. It is also live-only by nature: an async duel has players
  running days apart, with no turn to take. Nothing here blocks it — `QuestionKind` takes a fourth
  value and the nullable answer string already carries a pick — but it deserves its own spec.
- **Partial credit.** Seven of ten items in the right order scoring 70%, or a near-miss pin scoring
  less than a hit. Rejected for now because `Correct` becoming a fraction reaches into category
  accuracy, `TimesCorrect`, the difficulty ramp and what the results screen means by a correct answer.
- **A labelled map layer.** Needs ~200 countries × three languages with centroids for placement, and
  a review pass, since a wrong country name in Persian is the kind of error that stays wrong quietly.
- **Pan and zoom on the map**, and **pin drops on a real tiled map** — the latter would break offline
  play and add a tile provider.

## Order of work

Each step green before the next.

1. **Domain** — `QuestionKind`, the sort and map fields, per-kind validation and correctness, the
   nullable answer string on `AnswerRecord` and `LiveAnswer`. Existing tests keep passing as the
   `Choice` case.
2. **Persistence** — `QuestionDoc` serialises only `Choices` and `CorrectIndex` today and restores
   without a kind, so it gains explicit mapping for the kind and the map target. A legacy document
   has no kind field at all and must read back as `Choice`; that is a deliberate default, not
   something to leave to enum-zero coincidence.
3. **Selection, cards and reveals** — `QuestionSetBuilder` spanning kinds; the two card DTOs carrying
   the kind plus the shuffled items or the base layer while still never carrying the answer; the two
   reveal DTOs carrying a kind-appropriate answer instead of a bare `CorrectIndex`.
4. **Submission** — `AnswerDto`, `LiveHub.Answer`, `LiveMatch.Answer` and `Match.SubmitAnswer` taking
   the optional response, with the choice-range check scoped to `Choice`.
5. **The map asset** — the equirectangular SVG with ISO-coded paths, plus the projection helpers and
   their tests. Self-contained and independently verifiable.
6. **Play** — the sort list with its handle and keyboard path, the map component, and both reveals,
   in the live and async screens.
7. **Authoring** — the bucket key gaining `Kind`, the generator prompts with their constraints and
   the coordinate check, then the admin forms reusing the map component.
