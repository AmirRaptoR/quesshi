create table packs (
  tenant_id   uuid not null references tenants on delete cascade,
  id          uuid not null,
  owner_id    uuid,
  title       text not null,
  description text,
  lang        text not null,
  visibility  text not null default 'private' check (visibility in ('private', 'unlisted', 'tenant')),
  created_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now(),
  primary key (tenant_id, id),
  foreign key (tenant_id, owner_id) references players on delete cascade
);
create index packs_owner on packs (tenant_id, owner_id);

create table pack_items (
  tenant_id   uuid not null,
  pack_id     uuid not null,
  question_id uuid not null,
  position    int not null check (position >= 0),
  primary key (tenant_id, pack_id, question_id),
  unique (tenant_id, pack_id, position) deferrable initially deferred,
  foreign key (tenant_id, pack_id) references packs on delete cascade,
  foreign key (tenant_id, question_id) references questions on delete cascade
);
create index pack_items_question on pack_items (tenant_id, question_id);

select quesshi_tenant_table('packs', 'core');
select quesshi_tenant_table('pack_items', 'core');
