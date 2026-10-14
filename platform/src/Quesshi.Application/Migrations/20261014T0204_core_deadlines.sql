-- Cross-tenant deadline discovery is deliberately limited to the system sweep.
-- `skip` is supplied by each silo for calls it already owns or has backed off.
create function due_sessions(as_of timestamptz, max_rows int, skip uuid[])
  returns table (tenant_id uuid, id uuid, due_at timestamptz)
  language sql stable security definer set search_path = public
  as $due_sessions$
    select s.tenant_id, s.id, s.due_at
    from sessions s
    where s.due_at is not null
      and s.due_at <= as_of
      and (s.retry_at is null or s.retry_at <= as_of)
      and s.id <> all(coalesce(skip, '{}'::uuid[]))
    order by s.due_at, s.id
    limit greatest(0, least(coalesce(max_rows, 0), 256));
  $due_sessions$;

revoke all on function due_sessions(timestamptz, int, uuid[]) from public, quesshi_app;
grant execute on function due_sessions(timestamptz, int, uuid[]) to quesshi_system;
