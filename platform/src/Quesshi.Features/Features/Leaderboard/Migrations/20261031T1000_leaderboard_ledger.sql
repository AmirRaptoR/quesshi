-- One row per abandonment that counts: one per player per session, including a first one that
-- costs nothing. The seven-day tier is counted over these rows, by session end time, across
-- period boundaries. amount is the penalty as currently computed.
create table leaderboard_abandonments (
  tenant_id  uuid not null,
  player_id  uuid not null,
  session_id uuid not null,
  format     text not null,
  ended_at   timestamptz not null,
  ended_tz   text not null,
  amount     int not null default 0 check (amount >= 0),
  primary key (tenant_id, player_id, session_id),
  foreign key (tenant_id, player_id) references players on delete cascade
);
create index leaderboard_abandonments_order on leaderboard_abandonments (tenant_id, player_id, ended_at, session_id);

-- One row per thing that moved a player's score on a board in a period. A session's points and
-- an abandonment's penalty each appear once per board (the format's and 'all') and per period
-- ('all', the month, the week) of the session's end.
create table leaderboard_entries (
  tenant_id  uuid not null,
  board      text not null,
  period     text not null,
  player_id  uuid not null,
  session_id uuid not null,
  kind       text not null check (kind in ('points', 'penalty')),
  at         timestamptz not null,                       -- the session's end
  amount     int not null check (amount >= 0),           -- for a penalty: as currently computed (see architecture 13)
  primary key (tenant_id, board, period, player_id, session_id, kind),
  foreign key (tenant_id, player_id) references players on delete cascade
);
create index leaderboard_entries_order on leaderboard_entries (tenant_id, player_id, board, period, at, session_id);

-- The current score: the ledger folded in (at, session_id) order, and within one session points
-- before its penalty, adding points and subtracting penalties with a floor of zero at each step. last_at and last_session mark how far it is folded,
-- so an entry that arrives in order is applied directly and one that arrives late triggers a refold.
create table leaderboard_scores (
  tenant_id    uuid not null,
  board        text not null,
  period       text not null,
  player_id    uuid not null,
  score        bigint not null default 0 check (score >= 0),
  last_at      timestamptz,
  last_session uuid,
  primary key (tenant_id, board, period, player_id),
  foreign key (tenant_id, player_id) references players on delete cascade
);
create index leaderboard_top on leaderboard_scores (tenant_id, board, period, score desc, player_id);


select quesshi_tenant_table('leaderboard_scores', 'leaderboard');
select quesshi_tenant_table('leaderboard_abandonments', 'leaderboard');
select quesshi_tenant_table('leaderboard_entries', 'leaderboard');
