-- A question's picture, sound or video: {kind, url, attribution} as JSON, or null.
alter table questions add column media text;
