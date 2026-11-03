-- A player who signs in through the Telegram Mini App is found by their Telegram user id.
alter table players add column telegram_id text;
create unique index players_telegram_id on players (telegram_id) where telegram_id is not null;
