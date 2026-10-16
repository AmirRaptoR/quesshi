create table categories (
  tenant_id  uuid not null references tenants on delete cascade,
  id         uuid not null,
  slug       text not null,
  names      jsonb not null check (jsonb_typeof(names) = 'object'),
  icon       text not null default '',
  color      text not null default '',
  is_active  boolean not null default true,
  sort_order int not null default 0,
  primary key (tenant_id, id),
  unique (tenant_id, slug)
);

select quesshi_tenant_table('categories', 'core');
