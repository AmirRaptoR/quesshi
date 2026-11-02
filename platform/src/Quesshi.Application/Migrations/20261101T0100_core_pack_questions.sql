alter table questions drop constraint questions_source_check;
alter table questions add constraint questions_source_check
  check (source in ('seed', 'import', 'ai', 'admin', 'player', 'pack'));
