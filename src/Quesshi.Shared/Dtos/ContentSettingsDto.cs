namespace Quesshi.Shared;

public sealed record ContentSettingsDto(List<string> TriviaCategoryIds, List<string> VotingCategoryIds);
public sealed record AdminContentSettingsDto(List<CategoryDto> Categories, ContentSettingsDto Settings);
