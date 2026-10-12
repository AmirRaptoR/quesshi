create extension if not exists citext;
create extension if not exists pg_trgm;

create table schema_migrations (
  name       text primary key,
  owner      text not null,
  checksum   text not null,
  applied_at timestamptz not null default now()
);

revoke create on schema public from public;
grant usage, create on schema public to quesshi_owner;
grant usage on schema public to quesshi_app, quesshi_system;

alter default privileges revoke execute on functions from public;
alter default privileges revoke all on tables from public;

create function app_tenant() returns uuid
  language sql stable
  return current_setting('app.tenant_id')::uuid;

create function quesshi_tenant_table(t regclass, owner text) returns void
  language plpgsql as $$
begin
  if not exists (
    select 1 from pg_index i
    join pg_attribute a on a.attrelid = i.indrelid and a.attnum = i.indkey[0]
    where i.indrelid = t and i.indisprimary and a.attname = 'tenant_id'
  ) then
    raise exception 'table % must have a primary key starting with tenant_id', t;
  end if;
  execute format('comment on table %s is %L', t, 'owner:' || owner);
  execute format('revoke all on %s from public, quesshi_app, quesshi_system', t);
  execute format('alter table %s enable row level security', t);
  execute format(
    'create policy tenant_isolation on %s for all to quesshi_app
       using (tenant_id = (select app_tenant())) with check (tenant_id = (select app_tenant()))', t);
  execute format('grant select, insert, update, delete on %s to quesshi_app', t);
end $$;

revoke all on table schema_migrations from public, quesshi_app, quesshi_system;
revoke all on function app_tenant() from public, quesshi_system;
grant execute on function app_tenant() to quesshi_app;
revoke all on function quesshi_tenant_table(regclass, text) from public, quesshi_app, quesshi_system;
