-- One waiting entry per player per format.
create table matchmaking_entries (
  tenant_id    uuid not null,
  player_id    uuid not null,
  format       text not null,
  match_key    text not null,                            -- hash of the settings two players must share
  settings     jsonb not null check (jsonb_typeof(settings) = 'object'),   -- those settings, to build the session
  -- Set for formats where the waiting player already has a session others join (async duel).
  session_id   uuid,
  -- A short lease taken by a pairing attempt that must call the session's grain to finish.
  -- claimed_by is that attempt's own token; the entry is removed only by the attempt holding it.
  claimed_by   uuid,
  claimed_until timestamptz,
  enqueued_at  timestamptz not null default now(),
  primary key (tenant_id, player_id, format),
  foreign key (tenant_id, player_id) references players on delete cascade,
  foreign key (tenant_id, session_id) references sessions on delete cascade
);
create index matchmaking_queue on matchmaking_entries (tenant_id, format, match_key, enqueued_at);

select quesshi_tenant_table('matchmaking_entries', 'matchmaking');
