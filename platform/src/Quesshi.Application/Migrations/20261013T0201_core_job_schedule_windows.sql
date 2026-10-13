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

select quesshi_tenant_table('job_schedule_windows', 'core');
create policy system_all on job_schedule_windows for all to quesshi_system using (true) with check (true);
grant select, insert, update, delete on job_schedule_windows to quesshi_system;
