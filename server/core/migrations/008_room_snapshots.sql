-- Open lobbies and running games, so a restart can bring them back. Each room's data is its own
-- JSON snapshot; the row goes when the room ends.
create table room_snapshots (
  id text primary key,
  format text not null,
  data text not null
);
