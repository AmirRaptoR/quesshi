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
- **The review flow.** Questions are written by the OpenRouter pipeline (`TopUpQuestionBank`) or by
  hand in the admin panel, land as `QuestionStatus.Pending`, and become playable only once approved.
  `ReportsBeforeSuppressed = 3` retires one that players flag.
- **Deduplication.** `TopicKey.From(subject, aspect)` plus a unique `Lang + Topic` index stops the
  same question being written twice in one language.
- **Selection.** `QuestionSetBuilder.BuildAsync` picks by language, category and difficulty, and
  throws `NotEnoughQuestionsException` rather than quietly serving a different duel than the one
  that was asked for.

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

**Sorting reuses `Choices`.** The four items are stored in their correct order, and the card serves
them shuffled, so correctness is "does the submitted order match the stored one". No new field
holds the answer. The shuffle is seeded by `(matchId, slot)`, not by question id: everyone in a live
duel must see the same arrangement, and seeding by question alone would hand a returning player the
order they saw last time. The item count stays at `MatchRules.ChoicesPerQuestion`, which keeps the
20-second clock honest and reuses the bound the generator and admin already validate against.

**Map adds a nullable target and a base layer**, and leaves `Choices` empty. The target is one of two
shapes, so it is a small owned record rather than loose columns: a country carries an ISO 3166-1
alpha-2 code, a city carries a latitude, a longitude and a tolerance radius in kilometres. Only one
of the two is ever populated, and which one it is decides how an answer is checked. The base layer is
`Blank` or `Borders`.

A country target is rejected unless its code exists in the bundled SVG, so a question can never be
authored against a country the map cannot draw.

**The submitted answer gains one nullable string** beside `ChoiceIndex`, on both `AnswerRecord` and
`LiveAnswer`: `"2,0,3,1"` for a sort, `"DE"` or `"52.37,4.90"` for a map. `Choice` questions keep
using `ChoiceIndex` exactly as they do now, so every answer already stored in Redis and Mongo stays
readable and every existing code path is untouched. `Correct` stays a `bool`, computed per kind
inside the domain.

**Cards stay redacted.** A sort card carries the shuffled items; a map card carries the prompt and
the base layer. The correct order and the target are revealed only at reveal — the same rule the
two existing card DTOs already follow, extended rather than loosened.

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

**The generator gains a prompt per kind**, feeding the same pipeline and the same
`Pending → Approved` review. `TopicKey` and the unique `Lang + Topic` index are unchanged.

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

1. **Domain** — `QuestionKind`, the sort and map fields, per-kind correctness, the nullable answer
   string on `AnswerRecord` and `LiveAnswer`. Existing tests keep passing as the `Choice` case.
2. **Selection and cards** — `QuestionSetBuilder` spanning kinds, and the two card DTOs carrying the
   shuffled items or the base layer while still never carrying the answer.
3. **The map asset** — the equirectangular SVG with ISO-coded paths, plus the projection helpers and
   their tests. Self-contained and independently verifiable.
4. **Play** — the sort list with its handle and keyboard path, the map component, and both reveals,
   in the live and async screens.
5. **Authoring** — the generator prompts with their constraints and the coordinate check, then the
   admin forms reusing the map component.
