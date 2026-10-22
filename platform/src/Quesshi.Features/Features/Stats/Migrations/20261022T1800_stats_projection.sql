create table player_results (
  tenant_id  uuid not null,
  player_id  uuid not null,
  session_id uuid not null,
  format     text not null,
  ended_at   timestamptz not null,
  outcome    text not null check (outcome in ('win', 'loss', 'draw', 'none')),
  points     int not null default 0,
  primary key (tenant_id, player_id, session_id),
  foreign key (tenant_id, player_id) references players on delete cascade
);
create index player_results_recent on player_results (tenant_id, player_id, format, ended_at desc, session_id);

create table player_category_stats (
  tenant_id   uuid not null,
  player_id   uuid not null,
  category_id uuid not null,
  asked       int not null default 0 check (asked >= 0),
  credit_sum  double precision not null default 0 check (credit_sum >= 0),
  primary key (tenant_id, player_id, category_id),
  foreign key (tenant_id, player_id) references players on delete cascade,
  foreign key (tenant_id, category_id) references categories on delete cascade
);

create table player_stats (
  tenant_id   uuid not null,
  player_id   uuid not null,
  format      text not null,
  played      int not null default 0 check (played >= 0),
  wins        int not null default 0 check (wins >= 0),
  losses      int not null default 0 check (losses >= 0),
  draws       int not null default 0 check (draws >= 0),
  streak      int not null default 0 check (streak >= 0),
  best_streak int not null default 0 check (best_streak >= 0),
  points      bigint not null default 0 check (points >= 0),
  extra       jsonb not null default '{}' check (jsonb_typeof(extra) = 'object'),
  primary key (tenant_id, player_id, format),
  foreign key (tenant_id, player_id) references players on delete cascade
);

create table question_stats (
  tenant_id   uuid not null,
  question_id uuid not null,
  served      bigint not null default 0 check (served >= 0),
  asked       bigint not null default 0 check (asked >= 0),
  credit_sum  double precision not null default 0 check (credit_sum >= 0),
  primary key (tenant_id, question_id),
  foreign key (tenant_id, question_id) references questions on delete cascade
);

select quesshi_tenant_table('player_results', 'stats');
select quesshi_tenant_table('player_category_stats', 'stats');
select quesshi_tenant_table('player_stats', 'stats');
select quesshi_tenant_table('question_stats', 'stats');
