-- Control-plane configuration is intentionally global rather than registered through
-- quesshi_tenant_table: quesshi_system resolves hosts and provisions tenants.
create table tenants (
  id               uuid primary key,
  slug             text not null unique check (slug ~ '^[a-z0-9][a-z0-9-]*$'),
  name             text not null,
  status           text not null default 'active' check (status in ('active', 'suspended')),
  default_language text not null,
  timezone         text not null default 'UTC',
  branding         jsonb not null default '{}' check (jsonb_typeof(branding) = 'object'),
  config_version   bigint not null default 0,
  created_at       timestamptz not null default now()
);

create table tenant_languages (
  tenant_id    uuid not null references tenants on delete cascade,
  code         text not null,
  name         text not null,
  english_name text not null,
  direction    text not null default 'ltr' check (direction in ('ltr', 'rtl')),
  sort_order   int not null default 0,
  primary key (tenant_id, code)
);

create table tenant_hosts (
  host       text primary key check (host = lower(host) and host !~ '[:/]' and host !~ '\\.$'),
  tenant_id  uuid not null references tenants on delete cascade,
  is_primary boolean not null default false
);
create unique index tenant_hosts_primary on tenant_hosts (tenant_id) where is_primary;
create index tenant_hosts_tenant on tenant_hosts (tenant_id);

create table tenant_capabilities (
  tenant_id uuid not null references tenants on delete cascade,
  type      text not null check (type in ('kind', 'format', 'feature', 'signin', 'source', 'switch')),
  key       text not null,
  entitled  boolean not null default true,
  enabled   boolean not null default true,
  settings  jsonb not null default '{}' check (jsonb_typeof(settings) = 'object'),
  primary key (tenant_id, type, key)
);

revoke all on tenants, tenant_languages, tenant_hosts, tenant_capabilities from public, quesshi_app, quesshi_system;

alter table tenants enable row level security;
create policy own_tenant on tenants for all to quesshi_app
  using (id = (select app_tenant())) with check (id = (select app_tenant()));
create policy system_all on tenants for all to quesshi_system using (true) with check (true);
grant select on tenants to quesshi_app;
grant update (name, default_language, timezone, branding, config_version) on tenants to quesshi_app;
grant select, insert, update, delete on tenants to quesshi_system;

alter table tenant_languages enable row level security;
create policy own_tenant on tenant_languages for all to quesshi_app
  using (tenant_id = (select app_tenant())) with check (tenant_id = (select app_tenant()));
create policy system_all on tenant_languages for all to quesshi_system using (true) with check (true);
grant select, insert, update, delete on tenant_languages to quesshi_app, quesshi_system;

alter table tenant_hosts enable row level security;
create policy own_tenant on tenant_hosts for select to quesshi_app using (tenant_id = (select app_tenant()));
create policy system_all on tenant_hosts for all to quesshi_system using (true) with check (true);
grant select on tenant_hosts to quesshi_app;
grant select, insert, update, delete on tenant_hosts to quesshi_system;

alter table tenant_capabilities enable row level security;
create policy own_tenant on tenant_capabilities for all to quesshi_app
  using (tenant_id = (select app_tenant())) with check (tenant_id = (select app_tenant()));
create policy system_all on tenant_capabilities for all to quesshi_system using (true) with check (true);
grant select on tenant_capabilities to quesshi_app;
grant update (enabled, settings) on tenant_capabilities to quesshi_app;
grant select, insert, update, delete on tenant_capabilities to quesshi_system;
