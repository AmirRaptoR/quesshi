-- Content-addressed tenant media. Rows describe immutable files held outside PostgreSQL.
create table media_assets (
  tenant_id   uuid not null references tenants on delete cascade,
  id          uuid not null,
  kind        text not null check (kind in ('image', 'audio', 'video')),
  storage_key text not null,
  mime        text not null,
  bytes       bigint not null check (bytes >= 0),
  meta        jsonb not null default '{}' check (jsonb_typeof(meta) = 'object'),
  attribution text,
  created_at  timestamptz not null default now(),
  primary key (tenant_id, id),
  unique (tenant_id, storage_key)
);

alter table questions
  add column media_id uuid,
  add constraint questions_media_fk
    foreign key (tenant_id, media_id) references media_assets;

alter table session_items
  add constraint session_items_media_fk
    foreign key (tenant_id, media_id) references media_assets;

create index questions_media on questions (tenant_id, media_id) where media_id is not null;

select quesshi_tenant_table('media_assets', 'core');
