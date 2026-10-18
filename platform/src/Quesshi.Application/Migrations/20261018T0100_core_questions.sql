create table questions (
  tenant_id    uuid not null references tenants on delete cascade,
  id           uuid not null,
  kind         text not null,
  kind_version smallint not null default 1 check (kind_version > 0),
  family       text not null,
  lang         text not null,
  category_id  uuid,
  level        smallint check (level between 1 and 5),
  prompt       text not null,
  payload      jsonb not null check (jsonb_typeof(payload) = 'object'),
  explanation  text,
  topic        text,
  fingerprint  text,
  status       text not null default 'pending' check (status in ('draft', 'pending', 'approved', 'rejected')),
  source       text not null check (source in ('seed', 'import', 'ai', 'admin', 'player')),
  owner_id     uuid,
  created_at   timestamptz not null default now(),
  updated_at   timestamptz not null default now(),
  primary key (tenant_id, id),
  foreign key (tenant_id, category_id) references categories on delete set null (category_id),
  foreign key (tenant_id, owner_id) references players on delete cascade,
  check ((source = 'player') = (owner_id is not null))
);

create unique index questions_topic on questions (tenant_id, lang, family, topic)
  where topic is not null and owner_id is null;
create index questions_draw on questions (tenant_id, lang, kind, category_id, level)
  where status = 'approved' and owner_id is null;
create index questions_owner on questions (tenant_id, owner_id) where owner_id is not null;
create index questions_review on questions (tenant_id, status, created_at desc);
create index questions_prompt_trgm on questions using gin (prompt gin_trgm_ops);

alter table session_items
  add constraint session_items_question_fk
    foreign key (tenant_id, question_id) references questions on delete set null (question_id),
  add constraint session_items_category_fk
    foreign key (tenant_id, category_id) references categories on delete set null (category_id);

select quesshi_tenant_table('questions', 'core');
