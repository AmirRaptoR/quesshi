-- plugin: ai_generation
create table generation_runs (
  tenant_id   uuid not null references tenants on delete cascade,
  id          uuid not null,
  status      text not null check (status in ('running', 'finished', 'failed')),
  requested   int not null default 0 check (requested >= 0),
  accepted    int not null default 0 check (accepted >= 0),
  rejected    jsonb not null default '[]' check (jsonb_typeof(rejected) = 'array'),
  started_at  timestamptz not null default now(),
  finished_at timestamptz,
  primary key (tenant_id, id)
);

create table ai_budgets (
  tenant_id    uuid not null references tenants on delete cascade,
  month        text not null,
  limit_usd    numeric(12, 6) not null check (limit_usd >= 0),
  reserved_usd numeric(12, 6) not null default 0 check (reserved_usd >= 0),
  spent_usd    numeric(12, 6) not null default 0 check (spent_usd >= 0),
  primary key (tenant_id, month)
);

create table ai_calls (
  tenant_id     uuid not null references tenants on delete cascade,
  id            uuid not null,
  run_id        uuid,
  month         text not null,
  purpose       text not null,
  model         text not null,
  status        text not null default 'reserved'
                check (status in ('reserved', 'sent', 'done', 'failed', 'expired', 'unknown')),
  reserved_usd  numeric(12, 6) not null default 0 check (reserved_usd >= 0),
  input_tokens  int not null default 0 check (input_tokens >= 0),
  output_tokens int not null default 0 check (output_tokens >= 0),
  cost_usd      numeric(12, 6) not null default 0 check (cost_usd >= 0),
  at            timestamptz not null default now(),
  primary key (tenant_id, id),
  foreign key (tenant_id, run_id) references generation_runs on delete set null (run_id)
);
create index ai_calls_at on ai_calls (tenant_id, at desc);

select quesshi_tenant_table('generation_runs', 'ai_generation');
select quesshi_tenant_table('ai_budgets', 'ai_generation');
select quesshi_tenant_table('ai_calls', 'ai_generation');
