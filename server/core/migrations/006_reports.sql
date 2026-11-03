-- The questions a session served, by slot; reports and game history read them.
create table session_questions (
  session_id text not null references sessions (id),
  slot integer not null,
  question_id text not null,
  primary key (session_id, slot)
);
create index session_questions_question on session_questions (question_id);

-- One complaint per player and question.
create table question_reports (
  question_id text not null references questions (id) on delete cascade,
  player_id text not null,
  reason text not null,
  at text not null,
  primary key (question_id, player_id)
);
