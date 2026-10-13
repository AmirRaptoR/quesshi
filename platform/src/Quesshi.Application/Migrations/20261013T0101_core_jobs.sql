-- Work that happens outside a request. A worker leases a job, does it, and marks it done.
create table jobs (
  id           bigint generated always as identity primary key,
  tenant_id    uuid references tenants on delete cascade,
  owner        text not null default 'core',
  kind         text not null,
  payload      jsonb not null default '{}' check (jsonb_typeof(payload) = 'object'),
  unique_key   text,
  status       text not null default 'ready' check (status in ('ready', 'done', 'dead', 'cancelled')),
  run_at       timestamptz not null default now(),
  attempts     int not null default 0 check (attempts >= 0),
  max_attempts int default 10 check (max_attempts > 0),
  locked_by    uuid,
  locked_until timestamptz,
  last_error   text,
  created_at   timestamptz not null default now(),
  finished_at  timestamptz
);
create index jobs_ready on jobs (run_at) where status = 'ready';
create unique index jobs_unique on jobs (tenant_id, owner, kind, unique_key) nulls not distinct
  where unique_key is not null and status = 'ready';

-- quesshi_app can enqueue only for the tenant bound to its current transaction.
revoke all on jobs from public, quesshi_app, quesshi_system;
grant select, insert, update, delete on jobs to quesshi_system;

create function enqueue_job(job_owner text, job_kind text, job_payload jsonb,
                            job_unique_key text default null, job_run_at timestamptz default now())
  returns void
  language sql security definer set search_path = public
  as $enqueue$
    insert into jobs (tenant_id, owner, kind, payload, unique_key, run_at)
    values (app_tenant(), job_owner, job_kind, job_payload, job_unique_key, job_run_at)
    on conflict do nothing;
  $enqueue$;
grant execute on function enqueue_job(text, text, jsonb, text, timestamptz) to quesshi_app, quesshi_system;
