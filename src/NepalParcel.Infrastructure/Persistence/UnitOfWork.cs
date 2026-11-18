using Microsoft.EntityFrameworkCore;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;

namespace NepalParcel.Infrastructure.Persistence;

internal sealed class UnitOfWork(NepalParcelDbContext dbContext) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyException();
        }
    }

    public void DiscardPendingChanges() => dbContext.ChangeTracker.Clear();
}
