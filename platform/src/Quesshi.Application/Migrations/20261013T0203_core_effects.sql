-- Durable, tenant-scoped feature effects. Markers are intentionally permanent:
-- they make each feature's handling of an event idempotent across retries.
create table plugin_effects (
  tenant_id uuid not null references tenants on delete cascade,
  plugin text not null,
  event_key text not null,
  applied_at timestamptz not null default now(),
  primary key (tenant_id, plugin, event_key)
);
create index plugin_effects_age on plugin_effects (applied_at);

create table session_effects (
  tenant_id uuid not null,
  session_id uuid not null,
  revision bigint not null default 0 check (revision >= 0),
  primary key (tenant_id, session_id),
  foreign key (tenant_id, session_id) references sessions (tenant_id, id) on delete cascade
);

select quesshi_tenant_table(t, 'core')
from unnest(array['plugin_effects'::regclass, 'session_effects'::regclass]) as t;

-- The system worker owns job-state transitions and must advance revisions in
-- the same database transaction when a SessionEnded job becomes dead or reruns.
grant select, insert, update, delete on session_effects to quesshi_system;
create policy session_effects_system_all on session_effects
  for all to quesshi_system using (true) with check (true);

-- Extend the existing enqueue function rather than exposing jobs directly to
-- tenant code. Cleanup jobs use a NULL retry limit.
drop function enqueue_job(text, text, jsonb, text, timestamptz);
create function enqueue_job(
  job_owner text,
  job_kind text,
  job_payload jsonb,
  job_unique_key text default null,
  job_run_at timestamptz default now(),
  job_max_attempts int default 10)
  returns void
  language sql security definer set search_path = public
  as $enqueue$
    insert into jobs (tenant_id, owner, kind, payload, unique_key, run_at, max_attempts)
    values (app_tenant(), job_owner, job_kind, job_payload, job_unique_key, job_run_at, job_max_attempts)
    on conflict do nothing;
  $enqueue$;
grant execute on function enqueue_job(text, text, jsonb, text, timestamptz, int) to quesshi_app, quesshi_system;

-- Apps may read persisted event state only through this tenant-bound view of
-- the otherwise system-owned jobs table.
create function session_ended_effect_jobs(target_session_id uuid)
  returns table(plugin text, status text)
  language sql security definer set search_path = public
  as $jobs$
    select payload->>'feature', status
    from jobs
    where tenant_id = app_tenant()
      and owner = 'core'
      and kind = 'event'
      and payload->>'eventType' = 'sessionEnded'
      and (payload->>'subjectId')::uuid = target_session_id;
  $jobs$;
grant execute on function session_ended_effect_jobs(uuid) to quesshi_app, quesshi_system;
