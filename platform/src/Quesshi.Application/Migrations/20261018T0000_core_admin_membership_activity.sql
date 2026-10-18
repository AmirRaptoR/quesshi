-- Global administrator identities can be active while access to an individual tenant is disabled.
alter table admin_memberships
  add column is_active boolean not null default true;
