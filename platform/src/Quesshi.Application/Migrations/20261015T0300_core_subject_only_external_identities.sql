-- An external provider subject is sufficient to identify a live account. Email
-- remains unique when stored, but deletion must still scrub it.
alter table accounts drop constraint if exists accounts_check;
alter table accounts drop constraint if exists accounts_email_deleted_check;
alter table accounts add constraint accounts_email_deleted_check
  check (deleted_at is null or email is null);
