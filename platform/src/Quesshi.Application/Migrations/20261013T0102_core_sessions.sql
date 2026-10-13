-- Identity rows are global. Tenant-owned records refer to players rather than accounts.
create table accounts (
  id         uuid primary key,
  email      citext unique,
  created_at timestamptz not null default now(),
  deleted_at timestamptz,
  check ((email is null) = (deleted_at is not null))
);

create table players (
  tenant_id     uuid not null references tenants on delete cascade,
  id            uuid not null,
  account_id    uuid references accounts,
  display_name  text not null,
  avatar_seed   text not null,
  lang          text not null,
  banned_at     timestamptz,
  deleted_at    timestamptz,
  created_at    timestamptz not null default now(),
  last_seen_at  timestamptz,
  primary key (tenant_id, id),
  unique (tenant_id, account_id)
);
create index players_account on players (account_id) where account_id is not null;

create table sessions (
  tenant_id        uuid not null references tenants on delete cascade,
  id               uuid not null,
  code             text not null,
  format           text not null,
  owner_id         uuid not null,
  lang             text not null,
  status           text not null default 'lobby' check (status in ('lobby', 'running', 'finished', 'cancelled')),
  capacity         int not null check (capacity >= 1),
  content          jsonb not null check (jsonb_typeof(content) = 'object'),
  rules            jsonb not null default '{}' check (jsonb_typeof(rules) = 'object'),
  rules_version    smallint not null default 1 check (rules_version > 0),
  phase            text not null default 'lobby',
  slot             int not null default -1 check (slot >= -1),
  step             int not null default 0 check (step >= 0),
  phase_started_at timestamptz,
  revealed_slot    int not null default -1 check (revealed_slot >= -1),
  due_at           timestamptz,
  fault_count      smallint not null default 0 check (fault_count >= 0),
  retry_at         timestamptz,
  version          bigint not null default 0 check (version >= 0),
  state            jsonb not null default '{}' check (jsonb_typeof(state) = 'object'),
  state_version    smallint not null default 1 check (state_version > 0),
  result           jsonb not null default '{}' check (jsonb_typeof(result) = 'object'),
  result_version   smallint not null default 1 check (result_version > 0),
  rematch_of       uuid,
  end_reason       text,
  created_at       timestamptz not null default now(),
  started_at       timestamptz,
  ended_at         timestamptz,
  ended_tz         text,
  primary key (tenant_id, id),
  unique (tenant_id, rematch_of),
  foreign key (tenant_id, owner_id) references players,
  foreign key (tenant_id, rematch_of) references sessions,
  check (rematch_of is null or rematch_of <> id),
  check (status in ('lobby', 'running') or (state = '{}' and due_at is null)),
  check ((status in ('finished', 'cancelled')) = (ended_at is not null)),
  check (status in ('lobby', 'cancelled')
         or started_at is not null
         or (status = 'finished' and format = 'async_duel'
             and end_reason is not distinct from 'forfeit'))
);
create unique index sessions_code on sessions (tenant_id, code) where status in ('lobby', 'running');
create index sessions_due on sessions (due_at) where due_at is not null;

create table session_participants (
  tenant_id       uuid not null,
  session_id      uuid not null,
  player_id       uuid not null,
  seat            smallint not null check (seat >= 0),
  role            text not null default 'player' check (role in ('player', 'host', 'spectator')),
  status          text not null default 'active' check (status in ('active', 'done', 'left', 'eliminated')),
  phase           text not null default 'idle',
  slot            int not null default -1 check (slot >= -1),
  step            int not null default 0 check (step >= 0),
  slot_started_at timestamptz,
  revealed_slot   int not null default -1 check (revealed_slot >= -1),
  eligible        boolean not null default false,
  score           int not null default 0,
  place           smallint check (place > 0),
  outcome         text check (outcome in ('win', 'loss', 'draw', 'none')),
  ranked          boolean,
  state           jsonb not null default '{}' check (jsonb_typeof(state) = 'object'),
  result          jsonb not null default '{}' check (jsonb_typeof(result) = 'object'),
  joined_at       timestamptz not null default now(),
  left_at         timestamptz,
  primary key (tenant_id, session_id, player_id),
  unique (tenant_id, session_id, seat),
  foreign key (tenant_id, session_id) references sessions on delete cascade,
  foreign key (tenant_id, player_id) references players
);
create index participants_history on session_participants (tenant_id, player_id, joined_at desc, session_id);

create table session_items (
  tenant_id    uuid not null,
  session_id   uuid not null,
  slot         int not null check (slot >= 0),
  question_id  uuid,
  kind         text not null,
  kind_version smallint not null check (kind_version > 0),
  prompt       text not null,
  payload      jsonb not null check (jsonb_typeof(payload) = 'object'),
  resolved     jsonb not null default '{}' check (jsonb_typeof(resolved) = 'object'),
  card         jsonb not null check (jsonb_typeof(card) = 'object'),
  reveal       jsonb not null check (jsonb_typeof(reveal) = 'object'),
  media_id     uuid,
  explanation  text,
  level        smallint check (level between 1 and 5),
  category_id  uuid,
  category     jsonb check (jsonb_typeof(category) = 'object'),
  primary key (tenant_id, session_id, slot),
  foreign key (tenant_id, session_id) references sessions on delete cascade
);
create index session_items_question on session_items (tenant_id, question_id) where question_id is not null;
create index session_items_media on session_items (tenant_id, media_id) where media_id is not null;
create index session_items_category on session_items (tenant_id, category_id) where category_id is not null;

create table session_answers (
  tenant_id   uuid not null,
  session_id  uuid not null,
  slot        int not null,
  player_id   uuid not null,
  seq         int not null default 0 check (seq >= 0),
  revision    int not null default 0 check (revision >= 0),
  answer      jsonb,
  credit      real check (credit between 0 and 1),
  points      int not null default 0,
  taken_ms    int check (taken_ms >= 0),
  received_at timestamptz not null,
  primary key (tenant_id, session_id, slot, player_id, seq),
  foreign key (tenant_id, session_id, slot) references session_items on delete cascade,
  foreign key (tenant_id, session_id, player_id) references session_participants on delete cascade
);
create index session_answers_player on session_answers (tenant_id, player_id, session_id);

-- The content tables are introduced by later migrations. Keep the source ids indexed until then.
select quesshi_tenant_table(t, 'core') from unnest(array[
  'players', 'sessions', 'session_participants', 'session_items', 'session_answers'
]::regclass[]) as t;

revoke all on accounts from public, quesshi_app, quesshi_system;
alter table accounts enable row level security;
create policy members_only on accounts for select to quesshi_app
  using (exists (select 1 from players p
                 where p.account_id = accounts.id and p.tenant_id = (select app_tenant())));
create policy system_all on accounts for all to quesshi_system using (true) with check (true);
grant select on accounts to quesshi_app;
grant select, insert, update on accounts to quesshi_system;
