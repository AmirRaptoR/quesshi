create table sessions (
  id text primary key,
  code text not null,
  format text not null,
  status text not null,
  created_at text not null,
  ended_at text,
  ended_reason text,
  result text
);
create index sessions_code on sessions (code);

create table seats (
  session_id text not null references sessions (id),
  player_id text not null,
  primary key (session_id, player_id)
);

-- One row per player and question; a miss has no value.
create table answers (
  session_id text not null references sessions (id),
  player_id text not null,
  slot integer not null,
  value text,
  credit real not null,
  points integer not null,
  answered_at text,
  primary key (session_id, player_id, slot)
);
