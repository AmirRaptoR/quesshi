alter table auth_sessions add column revoked_at timestamptz;
create index auth_sessions_revoked on auth_sessions (tenant_id, revoked_at) where revoked_at is null;

-- The app role may revoke sessions only for a player in its current tenant. Keeping the row lets
-- request security return the stable banned error instead of treating the cookie as anonymous.
create function revoke_player_sessions(for_player uuid) returns void
  language plpgsql security definer set search_path = public as $$
declare
  current_tenant uuid := app_tenant();
  player_account uuid;
begin
  select account_id into player_account
  from players
  where tenant_id = current_tenant and id = for_player and deleted_at is null
  for key share;
  if not found then return; end if;

  update auth_sessions
  set revoked_at = now()
  where tenant_id = current_tenant and revoked_at is null
    and (guest_id = for_player or (player_account is not null and account_id = player_account));
end $$;

revoke all on function revoke_player_sessions(uuid) from public, quesshi_system;
grant execute on function revoke_player_sessions(uuid) to quesshi_app;
