-- The completed session must retain whether its format was scored. Stats runs after the format
-- and must not infer that fact from a player's separate leaderboard eligibility.
alter table sessions add column scored boolean not null default false;
