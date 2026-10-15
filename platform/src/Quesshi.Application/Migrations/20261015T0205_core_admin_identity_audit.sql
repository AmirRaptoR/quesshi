-- Admin identities are global state, while audit entries belong to one tenant.
create table admins (
  id                   uuid primary key,
  username             citext not null unique,
  email                citext not null unique,
  password_hash        text not null,
  is_active            boolean not null default true,
  last_login_at        timestamptz,
  is_platform          boolean not null default false,
  must_change_password boolean not null default false,
  failed_attempts      smallint not null default 0 check (failed_attempts >= 0),
  locked_until         timestamptz,
  created_at           timestamptz not null default now()
);

create table admin_memberships (
  tenant_id uuid not null references tenants on delete cascade,
  admin_id  uuid not null references admins on delete cascade,
  role      text not null default 'owner' check (role in ('owner', 'editor', 'moderator')),
  primary key (tenant_id, admin_id)
);
create index admin_memberships_admin on admin_memberships (admin_id);

create table admin_password_resets (
  token_hash bytea primary key,
  admin_id   uuid not null references admins on delete cascade,
  expires_at timestamptz not null,
  used_at    timestamptz
);
create index admin_password_resets_expiry on admin_password_resets (expires_at);

alter table auth_sessions
  add constraint auth_sessions_admin_id_fkey
  foreign key (admin_id) references admins on delete cascade;

create table audit_log (
  tenant_id   uuid not null references tenants on delete cascade,
  id          bigint generated always as identity,
  actor_type  text not null check (actor_type in ('admin', 'player', 'system')),
  actor_id    uuid,
  action      text not null,
  target_type text,
  target_id   uuid,
  data        jsonb not null default '{}' check (jsonb_typeof(data) = 'object'),
  at          timestamptz not null default now(),
  primary key (tenant_id, id)
);

select quesshi_tenant_table('audit_log', 'core');
revoke update, delete on audit_log from quesshi_app;
grant usage, select on sequence audit_log_id_seq to quesshi_app;

revoke all on admins, admin_memberships, admin_password_resets from public, quesshi_app, quesshi_system;
grant select, insert, update, delete on admins, admin_memberships, admin_password_resets to quesshi_system;
