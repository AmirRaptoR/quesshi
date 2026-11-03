create table admins (
  id text primary key,
  username text not null unique collate nocase,
  email text not null collate nocase,
  password_hash text not null,
  active integer not null default 1,
  must_change_password integer not null default 0,
  failed_attempts integer not null default 0,
  locked_until text,
  last_login_at text,
  created_at text not null
);

create table admin_sessions (
  token_hash text primary key,
  admin_id text not null references admins (id) on delete cascade,
  created_at text not null,
  expires_at text not null
);

-- Kept after use, so a second click on the same link is told it was already used.
create table admin_reset_tokens (
  secret_hash text primary key,
  admin_id text not null references admins (id) on delete cascade,
  issued_at text not null,
  used integer not null default 0
);
