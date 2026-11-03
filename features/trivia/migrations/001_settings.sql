-- Trivia's admin settings, one row each; a missing row means today's default.
create table trivia_settings (
  key text primary key,
  value text not null
);
