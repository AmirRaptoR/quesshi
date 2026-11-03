-- A player's own named set of private questions. The questions stay out of every shared draw:
-- they carry the status 'private', which only a draw from their own set reads.
create table question_sets (
  id text primary key,
  owner_id text not null references players (id),
  name text not null,
  lang text not null,
  created_at text not null
);
create index question_sets_owner on question_sets (owner_id, created_at);

alter table questions add column set_id text references question_sets (id) on delete cascade;
create index questions_set on questions (set_id);
