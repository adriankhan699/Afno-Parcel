using Microsoft.EntityFrameworkCore;
using NepalParcel.Application.Auth;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Identity;

namespace NepalParcel.Infrastructure.Persistence.Repositories;

internal sealed class RefreshTokenRepository(NepalParcelDbContext dbContext) : IRefreshTokenRepository
{
    public void Add(RefreshToken token) => dbContext.RefreshTokens.Add(token);

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        dbContext.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveByFamilyAsync(Guid familyId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        // Expiry is filtered in memory: a session has only a handful of tokens, and it keeps the query portable.
        var tokens = await dbContext.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return tokens.Where(t => t.IsActive(nowUtc)).ToList();
    }
}

internal sealed class CustomerRepository(NepalParcelDbContext dbContext) : ICustomerRepository
{
    public void Add(Customer customer) => dbContext.Customers.Add(customer);

    public Task<Customer?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        dbContext.Customers.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);

    public Task<Customer?> FindWalkInAsync(string normalizedPhone, string fullName, CancellationToken cancellationToken = default) =>
        dbContext.Customers.FirstOrDefaultAsync(
            c => c.UserId == null && c.PhoneNumber == normalizedPhone && c.FullName == fullName,
            cancellationToken);
}

internal sealed class AuditLogRepository(NepalParcelDbContext dbContext) : IAuditLogRepository
{
    public void Add(AuditLog entry) => dbContext.AuditLogs.Add(entry);
}
