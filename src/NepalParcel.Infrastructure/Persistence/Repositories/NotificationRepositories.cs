using Microsoft.EntityFrameworkCore;
using NepalParcel.Application.Notifications;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Integrations;
using NepalParcel.Domain.Notifications;

namespace NepalParcel.Infrastructure.Persistence.Repositories;

internal sealed class NotificationOutboxRepository(NepalParcelDbContext dbContext) : INotificationOutboxRepository
{
    public void Add(NotificationOutboxMessage message) => dbContext.NotificationOutbox.Add(message);

    public void Add(NotificationLog log) => dbContext.NotificationLogs.Add(log);

    public async Task<IReadOnlyList<Guid>> GetDueIdsAsync(DateTimeOffset nowUtc, int take, CancellationToken cancellationToken = default) =>
        await dbContext.NotificationOutbox
            .AsNoTracking()
            .Where(m =>
                ((m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Failed) && m.NextAttemptAtUtc <= nowUtc)
                || (m.Status == OutboxStatus.Processing && m.LockedUntilUtc != null && m.LockedUntilUtc <= nowUtc))
            .OrderBy(m => m.NextAttemptAtUtc)
            .Select(m => m.Id)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<NotificationOutboxMessage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.NotificationOutbox.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<NotificationOutboxMessage?> GetByProviderMessageIdAsync(string provider, string providerMessageId, CancellationToken cancellationToken = default) =>
        dbContext.NotificationOutbox.FirstOrDefaultAsync(m => m.Provider == provider && m.ProviderMessageId == providerMessageId, cancellationToken);

    public async Task<NotificationRow?> GetRowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await Rows().Where(x => x.Message.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : new NotificationRow(row.Message, row.Tracking);
    }

    public async Task<(IReadOnlyList<NotificationRow> Items, int TotalCount)> SearchAsync(NotificationFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = Rows();
        if (filter.Channel is { } channel)
        {
            query = query.Where(x => x.Message.Channel == channel);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(x => x.Message.Status == status);
        }

        if (filter.DeliveryStatus is { } delivery)
        {
            query = query.Where(x => x.Message.DeliveryStatus == delivery);
        }

        if (filter.Audience is { } audience)
        {
            query = query.Where(x => x.Message.Audience == audience);
        }

        if (filter.TemplateKey is { } key)
        {
            query = query.Where(x => x.Message.TemplateKey == key);
        }

        if (filter.ParcelId is { } parcelId)
        {
            query = query.Where(x => x.Message.ParcelId == parcelId);
        }

        if (filter.TrackingPrefix is { } prefix)
        {
            query = query.Where(x => x.Tracking != null && x.Tracking.StartsWith(prefix));
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(x => x.Message.CreatedAtUtc >= from);
        }

        if (filter.ToUtc is { } to)
        {
            query = query.Where(x => x.Message.CreatedAtUtc < to);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(x => x.Message.CreatedAtUtc)
            .ThenByDescending(x => x.Message.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ([.. rows.Select(r => new NotificationRow(r.Message, r.Tracking))], total);
    }

    public async Task<IReadOnlyList<NotificationRow>> ListForParcelAsync(Guid parcelId, CancellationToken cancellationToken = default)
    {
        var rows = await Rows()
            .Where(x => x.Message.ParcelId == parcelId)
            .OrderBy(x => x.Message.CreatedAtUtc)
            .ThenBy(x => x.Message.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(r => new NotificationRow(r.Message, r.Tracking))];
    }

    public async Task<IReadOnlyList<NotificationLog>> GetLogsAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) =>
        await dbContext.NotificationLogs
            .AsNoTracking()
            .Where(l => l.OutboxMessageId == outboxMessageId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ChannelCounts>> CountByChannelAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.NotificationOutbox
            .AsNoTracking()
            .Where(m => m.CreatedAtUtc >= fromUtc && m.CreatedAtUtc < toUtc)
            .GroupBy(m => m.Channel)
            .Select(g => new
            {
                Channel = g.Key,
                Total = g.Count(),
                Queued = g.Count(m => m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Processing),
                Retrying = g.Count(m => m.Status == OutboxStatus.Failed),
                Submitted = g.Count(m => m.Status == OutboxStatus.Submitted && m.DeliveryStatus == DeliveryStatus.None),
                Sent = g.Count(m => m.Status == OutboxStatus.Submitted && m.DeliveryStatus == DeliveryStatus.Sent),
                Delivered = g.Count(m => m.DeliveryStatus == DeliveryStatus.Delivered || m.DeliveryStatus == DeliveryStatus.Read),
                Read = g.Count(m => m.DeliveryStatus == DeliveryStatus.Read),
                Failed = g.Count(m => m.Status == OutboxStatus.DeadLetter || m.DeliveryStatus == DeliveryStatus.Failed),
                Skipped = g.Count(m => m.Status == OutboxStatus.Skipped || m.Status == OutboxStatus.Cancelled),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(r => new ChannelCounts(r.Channel, r.Total, r.Queued, r.Retrying, r.Submitted, r.Sent, r.Delivered, r.Read, r.Failed, r.Skipped))];
    }

    public Task<int> CountDeadLettersAsync(CancellationToken cancellationToken = default) =>
        dbContext.NotificationOutbox.CountAsync(m => m.Status == OutboxStatus.DeadLetter, cancellationToken);

    // A class with initialisers (not a record with a constructor): EF can keep filtering and sorting after projecting into it.
    private sealed class Row
    {
        public required NotificationOutboxMessage Message { get; init; }

        public string? Tracking { get; init; }
    }

    /// <summary>Each message with the tracking number of its parcel (if it has one), read-only.</summary>
    private IQueryable<Row> Rows() =>
        from message in dbContext.NotificationOutbox.AsNoTracking()
        join parcel in dbContext.Parcels.AsNoTracking() on message.ParcelId equals parcel.Id into parcels
        from parcel in parcels.DefaultIfEmpty()
        select new Row { Message = message, Tracking = parcel == null ? null : parcel.TrackingNumber };
}

internal sealed class NotificationTemplateRepository(NepalParcelDbContext dbContext) : INotificationTemplateRepository
{
    public async Task<IReadOnlyList<NotificationTemplate>> ListAsync(CancellationToken cancellationToken = default) =>
        await dbContext.NotificationTemplates.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<NotificationTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.NotificationTemplates.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<IReadOnlyList<NotificationTemplate>> FindActiveAsync(string key, string language, CancellationToken cancellationToken = default) =>
        await dbContext.NotificationTemplates
            .AsNoTracking()
            .Where(t => t.Key == key && t.Language == language && t.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<NotificationTemplate?> FindActiveAsync(string key, NotificationChannel channel, string language, CancellationToken cancellationToken = default) =>
        dbContext.NotificationTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Key == key && t.Channel == channel && t.Language == language && t.IsActive, cancellationToken);
}

internal sealed class NotificationOptOutRepository(NepalParcelDbContext dbContext) : INotificationOptOutRepository
{
    public async Task<IReadOnlyList<NotificationOptOut>> GetActiveAsync(IReadOnlyCollection<string> phoneNumbers, CancellationToken cancellationToken = default) =>
        await dbContext.NotificationOptOuts
            .AsNoTracking()
            .Where(o => o.RemovedAtUtc == null && phoneNumbers.Contains(o.PhoneNumber))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<NotificationOptOut?> FindActiveAsync(string phoneNumber, NotificationChannel channel, CancellationToken cancellationToken = default) =>
        dbContext.NotificationOptOuts.FirstOrDefaultAsync(o => o.PhoneNumber == phoneNumber && o.Channel == channel && o.RemovedAtUtc == null, cancellationToken);

    public Task<NotificationOptOut?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.NotificationOptOuts.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<NotificationOptOut> Items, int TotalCount)> SearchAsync(NotificationChannel? channel, bool activeOnly, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = dbContext.NotificationOptOuts.AsNoTracking().AsQueryable();
        if (channel is { } c)
        {
            query = query.Where(o => o.Channel == c);
        }

        if (activeOnly)
        {
            query = query.Where(o => o.RemovedAtUtc == null);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(o => o.CreatedAtUtc)
            .ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return (items, total);
    }

    public void Add(NotificationOptOut optOut) => dbContext.NotificationOptOuts.Add(optOut);
}

internal sealed class NotificationConsentRepository(NepalParcelDbContext dbContext) : INotificationConsentRepository
{
    public async Task<IReadOnlyList<CustomerConsent>> GetChannelConsentsAsync(Guid customerId, CancellationToken cancellationToken = default) =>
        await dbContext.CustomerConsents
            .AsNoTracking()
            .Where(c => c.CustomerId == customerId && (c.Type == ConsentType.SmsNotifications || c.Type == ConsentType.WhatsAppNotifications))
            .OrderByDescending(c => c.RecordedAtUtc)
            .ThenByDescending(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Add(CustomerConsent consent) => dbContext.CustomerConsents.Add(consent);
}

internal sealed class WebhookEventRepository(NepalParcelDbContext dbContext) : IWebhookEventRepository
{
    public Task<bool> ExistsAsync(string provider, string eventId, CancellationToken cancellationToken = default) =>
        dbContext.WebhookEvents.AnyAsync(e => e.Provider == provider && e.EventId == eventId, cancellationToken);

    public void Add(WebhookEvent webhookEvent) => dbContext.WebhookEvents.Add(webhookEvent);

    public async Task<IReadOnlyList<WebhookEvent>> GetPendingAsync(string provider, DateTimeOffset receivedAfterUtc, int take, CancellationToken cancellationToken = default) =>
        await dbContext.WebhookEvents
            .Where(e => e.Provider == provider && e.Status == WebhookStatus.Received && e.SignatureValid && e.ReceivedAtUtc >= receivedAfterUtc)
            .OrderBy(e => e.ReceivedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}

internal sealed class MockProviderMessageRepository(NepalParcelDbContext dbContext) : IMockProviderMessageRepository
{
    public void Add(MockProviderMessage message) => dbContext.MockProviderMessages.Add(message);

    public async Task<(IReadOnlyList<MockProviderMessage> Items, int TotalCount)> SearchAsync(NotificationChannel? channel, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = dbContext.MockProviderMessages.AsNoTracking().AsQueryable();
        if (channel is { } c)
        {
            query = query.Where(m => m.Channel == c);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(m => m.CreatedAtUtc)
            .ThenByDescending(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return (items, total);
    }
}
