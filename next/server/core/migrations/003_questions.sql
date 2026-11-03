create table categories (
  id text primary key,
  name_fa text not null,
  name_en text not null,
  name_nl text not null default '',
  icon text not null,
  color text not null,
  active integer not null default 1,
  sort_order integer not null default 0
);

-- The envelope every kind shares; payload is the kind's own JSON, read only by the feature that owns the kind.
create table questions (
  id text primary key,
  kind text not null,
  lang text not null,
  category_id text references categories (id),
  level integer,
  prompt text not null,
  payload text not null,
  explanation text,
  status text not null,
  created_at text not null
);
create index questions_draw on questions (lang, status, category_id, level);
