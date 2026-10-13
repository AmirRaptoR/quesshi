# Async duel legacy Match test port

`MatchTests` exercises today's two-player aggregate, which owns the lobby,
question drawing, and snapshot migration concerns.  `AsyncDuelFormat` runs
inside the platform engine instead, so its tests cover player-run behavior at
the format boundary and leave aggregate and engine responsibilities at their
existing boundaries.

## Ported behavior

The following legacy behaviors are covered by
`platform/tests/Quesshi.Formats.Tests/AsyncDuelFormatTests.cs`:

| Legacy `MatchTests` behavior | Async-duel test coverage |
| --- | --- |
| `A_match_refuses_a_length_nobody_can_choose` | `Declares_the_personal_async_duel_contract_and_draws_supported_fixed_counts` |
| `A_run_finishes_after_however_many_questions_the_match_holds` | `A_run_finishes_after_every_supported_question_count` |
| `The_challenger_can_play_before_anyone_joins` | `A_creator_can_finish_before_a_later_player_joins_without_resetting_the_session` |
| `A_stranger_cannot_play_a_match_they_are_not_in`, `Answering_a_question_that_was_never_served_is_rejected`, `Answering_out_of_order_is_rejected`, `Answering_the_same_question_twice_is_rejected`, `Playing_on_after_finishing_the_run_is_rejected`, `SubmitAnswer_refuses_an_answer_once_the_match_is_terminal` | `Personal_question_expiry_is_lazy_and_terminal_or_nonplayer_commands_are_stale` and `Records_one_timely_answer_then_releases_and_advances_only_on_next` |
| `Both_players_face_the_same_questions_in_the_same_order` | `A_creator_can_finish_before_a_later_player_joins_without_resetting_the_session` (fixed drawn items survive the later join) |
| `A_players_answers_stay_hidden_until_the_other_has_finished` | `Views_keep_other_runs_private_until_the_viewer_is_done_and_keep_own_card_and_reveal_scoped` |
| `The_match_resolves_when_both_runs_are_finished`, `A_lobby_with_room_for_more_does_not_resolve_until_every_seat_is_finished_playing` | `Completes_only_after_two_player_runs_and_uses_competition_places_and_outcomes` |
| `Equal_scores_resolve_to_a_draw`, `Standings_share_first_place_when_two_finishers_tie_at_the_top`, `Standings_give_a_sole_winner_when_nobody_ties_the_top_score` | `Completes_only_after_two_player_runs_and_uses_competition_places_and_outcomes` |
| `A_late_answer_scores_nothing_but_still_advances_the_run` | `Grace_is_judged_from_receipt_time_and_expiry_records_one_miss_on_the_next_command` and `Uses_shared_scoring_for_correct_incorrect_grace_period_and_weighted_answers` |
| `An_untouched_match_forfeits_after_the_deadline`, `Forfeiture_finalises_standings_from_whatever_each_player_banked` | `Forfeit_is_anchored_to_creation_and_keeps_finished_runs_while_marking_unfinished_seats_left`, `A_forfeit_ranks_an_unfinished_run_by_its_banked_score`, and `A_forfeit_does_not_count_an_expired_slot_as_answered` |
| `A_resolved_match_cannot_be_forfeited` | `A_completed_session_cannot_be_forfeited` |

## Explicit exclusions

The following legacy tests are deliberately not copied into the format suite.
They remain important coverage, but belong to the indicated boundary rather
than an async-duel format transition.

### Lobby or engine responsibility

These are session creation, ownership, capacity, joining, starting, leaving,
cancellation, pairing, question-draw, or clock-settlement behaviors owned by
the platform engine/session layer:

- `A_new_match_waits_for_an_opponent`
- `The_challenger_cannot_join_their_own_match`
- `A_third_player_cannot_join_a_taken_match`
- `Join_leaves_a_two_seat_lobby_open_once_it_is_full`
- `A_third_join_into_a_full_two_seat_lobby_is_refused`
- `A_full_lobby_reopens_once_a_non_owner_leaves_and_the_owner_can_still_start_it`
- `A_fresh_lobby_holds_only_its_owner`
- `Create_rejects_a_capacity_outside_two_to_five_hundred`
- `ServeNext_is_refused_while_the_question_set_is_undrawn`
- `DrawQuestions_cannot_be_called_once_the_duel_has_started`
- `Start_by_the_owner_with_two_seated_begins_the_duel_with_room_to_spare`
- `Start_is_refused_below_two_participants`
- `Start_is_refused_for_a_non_owner`
- `Start_is_refused_once_the_lobby_has_already_started`
- `Start_is_refused_while_the_question_set_is_still_undrawn`
- `Leave_frees_a_non_owner_seat`
- `Leave_is_refused_for_the_owner`
- `Leave_is_refused_once_the_duel_has_started`
- `Cancel_by_the_owner_ends_the_lobby_as_no_contest_with_no_ownership_transfer`
- `Cancel_is_refused_for_a_non_owner`
- `Cancel_is_refused_once_the_duel_has_started`
- `UpdateSettings_changes_settings_while_the_question_set_is_still_empty`
- `UpdateSettings_cannot_change_the_game_mode`
- `UpdateSettings_is_refused_once_the_question_set_is_drawn`
- `UpdateSettings_is_refused_for_a_non_owner`
- `The_owner_widens_capacity_in_the_lobby_phase`
- `SetCapacity_is_refused_for_a_non_owner`
- `SetCapacity_is_refused_outside_two_to_five_hundred`
- `SetCapacity_is_refused_below_the_seated_count`
- `SetCapacity_is_refused_once_the_duel_has_started`
- `StartByPairing_begins_the_duel_once_two_are_seated_and_questions_are_drawn`
- `StartByPairing_is_refused_below_two_participants`
- `StartByPairing_is_refused_once_the_lobby_has_already_started`
- `A_widened_lobby_can_seat_more_than_the_original_capacity`
- `Join_settles_the_clock_first_so_an_expired_lobby_cannot_be_joined`
- `IsOver_includes_no_contest_so_a_cancelled_lobby_reads_as_over`

### Snapshot and legacy-restore responsibility

These test persistence compatibility of the legacy aggregate.  Platform
format state is persisted through the versioned session contract and is
covered by its contract tests instead:

- `A_snapshot_written_without_a_run_length_restores_intact`
- `A_legacy_snapshot_deserializes_and_restores_the_two_player_shape`
- `A_legacy_lobby_nobody_joined_keeps_its_free_seat`
- `A_legacy_records_settings_are_read_only_because_its_questions_are_already_drawn`
- `Both_snapshot_shapes_round_trip_through_JSON`

### Superseded by the per-seat tie correction

`Nobody_played_so_a_forfeit_leaves_no_winner` is intentionally superseded by
architecture section 19.4.  Async duels build score-based standings for every
seat, including a zero-score tie; `A_zero_score_forfeit_ranks_all_players_as_a_draw_not_no_contest`
verifies that corrected behavior.
