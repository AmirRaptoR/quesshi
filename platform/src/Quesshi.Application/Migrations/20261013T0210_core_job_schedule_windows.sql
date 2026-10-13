-- A completed scheduled job may share its key with future ad-hoc jobs, so preserve
-- the scheduler's per-tenant window claim independently of the jobs lifecycle.
create table job_schedule_windows (
  tenant_id      uuid not null references tenants on delete cascade,
  owner          text not null,
  kind           text not null,
  interval_ticks bigint not null check (interval_ticks > 0),
  window_start   timestamptz not null,
  primary key (tenant_id, owner, kind, interval_ticks, window_start)
);

revoke all on job_schedule_windows from public, quesshi_app, quesshi_system;
grant select, insert, update, delete on job_schedule_windows to quesshi_system;
