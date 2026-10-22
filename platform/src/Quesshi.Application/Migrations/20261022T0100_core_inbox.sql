-- Durable per-player versions are separate from players so that collecting an inbox
-- change does not take a player-row lock. TenantDb applies them last at commit.
create table player_inbox (
  tenant_id uuid not null,
  player_id uuid not null,
  version   bigint not null default 0 check (version >= 0),
  primary key (tenant_id, player_id),
  foreign key (tenant_id, player_id) references players on delete cascade
);

create table session_invites (
  tenant_id  uuid not null,
  session_id uuid not null,
  player_id  uuid not null,
  invited_by uuid not null,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null,
  primary key (tenant_id, session_id, player_id),
  foreign key (tenant_id, session_id) references sessions on delete cascade,
  foreign key (tenant_id, player_id) references players on delete cascade,
  foreign key (tenant_id, invited_by) references players on delete cascade
);
create index session_invites_player on session_invites (tenant_id, player_id);

select quesshi_tenant_table(t, 'core')
from unnest(array['player_inbox'::regclass, 'session_invites'::regclass]) as t;
