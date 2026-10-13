-- Global identity state is owned by the system role; tenant request limits are
-- registered as ordinary tenant-owned data.
create table account_identities (
  provider   text not null,
  subject    text not null,
  account_id uuid not null references accounts on delete cascade,
  primary key (provider, subject)
);
create index account_identities_account on account_identities (account_id);

create table login_codes (
  email      citext primary key,
  code_hash  bytea not null,
  attempts   smallint not null default 0 check (attempts >= 0),
  expires_at timestamptz not null,
  created_at timestamptz not null default now()
);

create table auth_sessions (
  token_hash   bytea primary key,
  tenant_id    uuid not null references tenants on delete cascade,
  account_id   uuid references accounts on delete cascade,
  guest_id     uuid,
  -- Admin identity records are introduced by their own forward migration; the
  -- stable session subject column is available now without that dependency.
  admin_id     uuid,
  created_at   timestamptz not null default now(),
  last_used_at timestamptz not null default now(),
  expires_at   timestamptz not null,
  foreign key (tenant_id, guest_id) references players on delete cascade,
  check (num_nonnulls(account_id, guest_id, admin_id) = 1)
);
create index auth_sessions_account on auth_sessions (account_id) where account_id is not null;
create index auth_sessions_guest on auth_sessions (tenant_id, guest_id) where guest_id is not null;
create index auth_sessions_admin on auth_sessions (admin_id) where admin_id is not null;
create index auth_sessions_expiry on auth_sessions (expires_at);

create table auth_rate_limits (
  key          text not null,
  window_start timestamptz not null,
  count        int not null default 0 check (count >= 0),
  primary key (key, window_start)
);

create table rate_limits (
  tenant_id    uuid not null references tenants on delete cascade,
  key          text not null,
  window_start timestamptz not null,
  count        int not null default 0 check (count >= 0),
  primary key (tenant_id, key, window_start)
);

create table data_protection_keys (
  id            bigint generated always as identity primary key,
  friendly_name text not null unique,
  xml           text not null,
  created_at    timestamptz not null default now()
);

select quesshi_tenant_table('rate_limits', 'core');

-- Preserve the original policy name while ensuring tombstoned profiles no
-- longer reveal their associated global account to a tenant.
alter policy members_only on accounts
  using (exists (select 1 from players p
                where p.account_id = accounts.id
                  and p.tenant_id = (select app_tenant())
                  and p.deleted_at is null));

revoke all on account_identities, login_codes, auth_sessions, auth_rate_limits,
  data_protection_keys from public, quesshi_app, quesshi_system;
grant select, insert, update, delete on account_identities, login_codes, auth_sessions,
  auth_rate_limits, data_protection_keys to quesshi_system;
grant usage, select on sequence data_protection_keys_id_seq to quesshi_system;

create function account_players(for_account uuid)
  returns table (tenant_id uuid, player_id uuid, display_name text, avatar_seed text, lang text,
                 last_seen_at timestamptz)
  language sql stable security definer set search_path = public
  as $$
    select p.tenant_id, p.id, p.display_name, p.avatar_seed, p.lang, p.last_seen_at
    from players p where p.account_id = for_account and p.deleted_at is null;
  $$;

create function claim_account(for_account uuid) returns void
  language plpgsql security definer set search_path = public as $$
begin
  perform 1 from accounts a where a.id = for_account and a.deleted_at is null for key share;
  if not found then
    raise exception 'account % is not available', for_account using errcode = 'P0002';
  end if;
end $$;

revoke all on function account_players(uuid) from public, quesshi_app, quesshi_system;
revoke all on function claim_account(uuid) from public, quesshi_app, quesshi_system;
grant execute on function account_players(uuid) to quesshi_system;
grant execute on function claim_account(uuid) to quesshi_app;
