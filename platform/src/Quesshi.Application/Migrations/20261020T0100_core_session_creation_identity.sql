alter table sessions
  add column creation_shape text not null default 'lobby' check (creation_shape in ('lobby', 'started', 'open')),
  add column creation_roster jsonb not null default '[]'::jsonb check (jsonb_typeof(creation_roster) = 'array');
