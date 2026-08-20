using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

/// <remarks>Extra elements are ignored so a removed field cannot break start-up.</remarks>
[BsonIgnoreExtraElements]
public sealed class PlayerDoc
{
    [BsonId] public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string AvatarSeed { get; set; } = "";
    public int Lang { get; set; }
    public bool IsBanned { get; set; }
    public bool IsGuest { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Draws { get; set; }
    public int Streak { get; set; }
    public int BestStreak { get; set; }
    public long TotalScore { get; set; }
    public Dictionary<string, int[]> ByCategory { get; set; } = [];
    public List<string> Friends { get; set; } = [];

    public static PlayerDoc From(Player p)
    {
        var s = p.ToSnapshot();
        return new PlayerDoc
        {
            Id = s.Id, Email = s.Email, DisplayName = s.DisplayName, AvatarSeed = s.AvatarSeed, Lang = (int)s.Lang,
            IsBanned = s.IsBanned, IsGuest = s.IsGuest, CreatedAt = s.CreatedAt.UtcDateTime,
            Wins = s.Stats.Wins, Losses = s.Stats.Losses, Draws = s.Stats.Draws,
            Streak = s.Stats.Streak, BestStreak = s.Stats.BestStreak, TotalScore = s.Stats.TotalScore,
            ByCategory = s.ByCategory.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.Asked, kv.Value.Correct }),
            Friends = s.Friends
        };
    }

    public Player ToDomain() => Player.FromSnapshot(new PlayerSnapshot(
        Id, Email, DisplayName, AvatarSeed, (Language)Lang, IsBanned, new DateTimeOffset(CreatedAt, TimeSpan.Zero),
        new PlayerStats(Wins, Losses, Draws, Streak, BestStreak, TotalScore),
        ByCategory.ToDictionary(kv => kv.Key, kv => new CategoryRecord(kv.Value[0], kv.Value.Length > 1 ? kv.Value[1] : 0)),
        Friends, IsGuest));
}
