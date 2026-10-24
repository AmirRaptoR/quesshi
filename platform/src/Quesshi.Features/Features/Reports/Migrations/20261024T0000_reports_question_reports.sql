create table question_reports (
  tenant_id   uuid not null,
  question_id uuid not null,
  player_id   uuid not null,
  reason      text not null,
  note        text,
  created_at  timestamptz not null default now(),
  primary key (tenant_id, question_id, player_id),
  foreign key (tenant_id, question_id) references questions on delete cascade,
  foreign key (tenant_id, player_id) references players on delete cascade
);
create index question_reports_player on question_reports (tenant_id, player_id);

select quesshi_tenant_table('question_reports', 'reports');
