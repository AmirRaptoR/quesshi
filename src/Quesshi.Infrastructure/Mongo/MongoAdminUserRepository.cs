using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoAdminUserRepository(MongoContext db) : IAdminUserRepository
{
    public async Task<AdminUser?> GetAsync(string id, CancellationToken ct = default)
        => (await db.AdminUsers.Find(a => a.Id == id).FirstOrDefaultAsync(ct))?.ToDomain();

    public async Task<AdminUser?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        var folded = username.Trim().ToLowerInvariant();
        return (await db.AdminUsers.Find(a => a.Username == folded).FirstOrDefaultAsync(ct))?.ToDomain();
    }

    public async Task<AdminUser?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        var folded = email.Trim().ToLowerInvariant();
        return (await db.AdminUsers.Find(a => a.Email == folded).FirstOrDefaultAsync(ct))?.ToDomain();
    }

    public async Task<IReadOnlyList<AdminUser>> AllAsync(CancellationToken ct = default)
        => [.. (await db.AdminUsers.Find(Builders<AdminUserDoc>.Filter.Empty).SortBy(a => a.Username).ToListAsync(ct)).Select(d => d.ToDomain())];

    public Task<long> CountAsync(CancellationToken ct = default)
        => db.AdminUsers.CountDocumentsAsync(Builders<AdminUserDoc>.Filter.Empty, cancellationToken: ct);

    public Task UpsertAsync(AdminUser user, CancellationToken ct = default)
        => db.AdminUsers.ReplaceOneAsync(a => a.Id == user.Id, AdminUserDoc.From(user), new ReplaceOptions { IsUpsert = true }, ct);

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => db.AdminUsers.DeleteOneAsync(a => a.Id == id, ct);
}
