namespace NepalParcel.Application.Common.Interfaces;

/// <summary>Commits all changes tracked during the current request as one transaction.</summary>
public interface IUnitOfWork
{
    /// <exception cref="Exceptions.ConcurrencyException">Another user changed the same record first.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets all unsaved changes (used to recover after a failed save).</summary>
    void DiscardPendingChanges();
}
