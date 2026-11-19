using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Parcel status history is append-only. This refuses any save that would change or delete an existing history row, so
/// a bug (or a careless future change) can never rewrite what happened to a parcel. New rows are always allowed.
/// </summary>
internal sealed class ParcelHistoryGuardInterceptor : SaveChangesInterceptor
{
    public const string Message = "Parcel status history is append-only: existing rows cannot be changed or deleted.";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Check(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Check(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Check(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var tampered = context.ChangeTracker
            .Entries<ParcelStatusHistory>()
            .Any(e => e.State is EntityState.Modified or EntityState.Deleted);
        if (tampered)
        {
            throw new InvalidOperationException(Message);
        }
    }
}
