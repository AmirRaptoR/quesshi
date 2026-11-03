create table players (
  id text primary key,
  email text unique,
  display_name text not null,
  avatar_seed text not null,
  lang text not null,
  guest integer not null default 0,
  banned_at text,
  created_at text not null
);

-- The latest code requested for an address; requesting another replaces it.
create table login_codes (
  email text primary key,
  code_hash text not null,
  issued_at text not null,
  attempts integer not null default 0
);

create table auth_sessions (
  token_hash text primary key,
  player_id text not null references players (id),
  created_at text not null,
  expires_at text not null
);
