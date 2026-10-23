-- Mutual friendships are stored as two directed rows in one tenant transaction.
create table friendships (
  tenant_id  uuid not null,
  player_id  uuid not null,
  friend_id  uuid not null,
  created_at timestamptz not null default now(),
  primary key (tenant_id, player_id, friend_id),
  foreign key (tenant_id, player_id) references players on delete cascade,
  foreign key (tenant_id, friend_id) references players on delete cascade,
  check (player_id <> friend_id)
);
create index friendships_reverse on friendships (tenant_id, friend_id);

select quesshi_tenant_table('friendships', 'friends');
