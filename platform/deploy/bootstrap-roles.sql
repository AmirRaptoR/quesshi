-- These roles are deliberately separate. Object privileges are granted by migrations;
-- this bootstrap grants only the owner's migration prerequisites.
do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'quesshi_owner') then
    create role quesshi_owner;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'quesshi_app') then
    create role quesshi_app;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'quesshi_system') then
    create role quesshi_system;
  end if;
end $$;

alter role quesshi_owner login nosuperuser nobypassrls noinherit nocreatedb nocreaterole noreplication;
alter role quesshi_app login nosuperuser nobypassrls noinherit nocreatedb nocreaterole noreplication;
alter role quesshi_system login nosuperuser nobypassrls noinherit nocreatedb nocreaterole noreplication;

revoke quesshi_owner from quesshi_app, quesshi_system;
revoke quesshi_app from quesshi_owner, quesshi_system;
revoke quesshi_system from quesshi_owner, quesshi_app;

do $$
begin
  execute format('grant create on database %I to quesshi_owner', current_database());
end $$;

grant usage, create on schema public to quesshi_owner;
