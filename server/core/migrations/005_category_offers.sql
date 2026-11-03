-- Which categories each feature offers players; today's TriviaCategoryIds and VotingCategoryIds.
create table category_offers (
  feature text not null,
  category_id text not null references categories (id) on delete cascade,
  primary key (feature, category_id)
);
