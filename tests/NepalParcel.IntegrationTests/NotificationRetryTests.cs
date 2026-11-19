using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Application.Notifications;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Notifications;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;
using static NepalParcel.IntegrationTests.Infrastructure.NotificationTestSupport;

namespace NepalParcel.IntegrationTests;

/// <summary>
/// Sending when the provider misbehaves: retries with exponential backoff, giving up, permanent refusals, a provider that throws,
/// a worker that died mid-send, and an administrator giving up messages a second chance. The provider here is a script.
/// </summary>
public sealed class NotificationRetryTests(ScriptedProviderFactory factory) : IClassFixture<ScriptedProviderFactory>
{
    private readonly BookingTestHelpers _helpers = new(factory);

    /// <summary>
    /// A booking whose only waiting message is the receiver's SMS (the sender gave no consent, and the receiver's WhatsApp is
    /// switched off by opting the number out), so a test scripts exactly one send.
    /// </summary>
    private async Task<NotificationOutboxMessage> QueueOneSmsAsync()
    {
        await ProcessAllAsync(factory);
        var phone = NewPhone();
        var admin = await AdminAsync(factory);
        (await admin.PostAsJsonAsync(new Uri("/api/v1/notifications/opt-outs", UriKind.Relative), new CreateOptOutRequest(phone, "WhatsApp"))).EnsureSuccessStatusCode();
        var branch = await _helpers.CreateBranchAsync();
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        var parcel = await BookingTestHelpers.BookAsync(counter, NewBooking(branch, senderName: NewName("Sender"), receiverPhone: phone));
        var message = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms);
        Assert.Equal(OutboxStatus.Pending, message.Status);
        return message;
    }

    private async Task<NotificationOutboxMessage> ReloadAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        return await db.NotificationOutbox.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    [Fact]
    public async Task ATemporaryFailure_IsRetriedLater_NotAtOnce_AndTheAttemptsAreRecorded()
    {
        var queued = await QueueOneSmsAsync();
        factory.Sms.Then(SendResult.RetryLater("gateway timeout"));

        var first = await ProcessAllAsync(factory);
        var failed = await ReloadAsync(queued.Id);

        Assert.Equal(1, first.Retrying);
        Assert.Equal(OutboxStatus.Failed, failed.Status);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal("gateway timeout", failed.LastError);
        Assert.Equal(DeliveryStatus.None, failed.DeliveryStatus);
        var wait = failed.NextAttemptAtUtc - DateTimeOffset.UtcNow;
        Assert.InRange(wait.TotalSeconds, 15, 40);

        // Running again straight away does nothing: the message is waiting out its delay.
        var immediately = await ProcessAllAsync(factory);
        Assert.Equal(0, immediately.Claimed);
        Assert.Equal(1, (await ReloadAsync(queued.Id)).AttemptCount);

        await MakeDueAsync(factory, queued.Id);
        var second = await ProcessAllAsync(factory);
        var sent = await ReloadAsync(queued.Id);

        Assert.Equal(1, second.Submitted);
        Assert.Equal(OutboxStatus.Submitted, sent.Status);
        Assert.Equal(2, sent.AttemptCount);
        Assert.Null(sent.LastError);
        var logs = await LogsAsync(factory, queued.Id);
        Assert.Equal([NotificationLogStatus.Failed, NotificationLogStatus.Submitted], logs.Select(l => l.Status).ToArray());
        Assert.Equal([1, 2], logs.Select(l => l.AttemptNumber).ToArray());
        Assert.Equal("gateway timeout", logs[0].Error);
        Assert.Equal(sent.ProviderMessageId, logs[1].ProviderMessageId);
    }

    [Fact]
    public async Task TheWaitBetweenRetries_Doubles_AndTheMessageIsFinallyGivenUp()
    {
        var queued = await QueueOneSmsAsync();
        factory.Sms.Then(SendResult.RetryLater("down 1"), SendResult.RetryLater("down 2"), SendResult.RetryLater("down 3"));

        await ProcessAllAsync(factory);
        var afterFirst = await ReloadAsync(queued.Id);
        var firstWait = afterFirst.NextAttemptAtUtc - DateTimeOffset.UtcNow;
        await MakeDueAsync(factory, queued.Id);
        await ProcessAllAsync(factory);
        var afterSecond = await ReloadAsync(queued.Id);
        var secondWait = afterSecond.NextAttemptAtUtc - DateTimeOffset.UtcNow;
        await MakeDueAsync(factory, queued.Id);
        var last = await ProcessAllAsync(factory);
        var gaveUp = await ReloadAsync(queued.Id);

        Assert.InRange(firstWait.TotalSeconds, 15, 40);
        Assert.InRange(secondWait.TotalSeconds, 40, 75);
        Assert.True(secondWait > firstWait);
        Assert.Equal(1, last.Failed);
        Assert.Equal(OutboxStatus.DeadLetter, gaveUp.Status);
        Assert.Equal(3, gaveUp.AttemptCount);
        Assert.Equal("down 3", gaveUp.LastError);
        Assert.Equal(DeliveryStatus.None, gaveUp.DeliveryStatus);
        var logs = await LogsAsync(factory, queued.Id);
        Assert.Equal(3, logs.Count);
        Assert.All(logs, l => Assert.Equal(NotificationLogStatus.Failed, l.Status));
        Assert.Equal([1, 2, 3], logs.Select(l => l.AttemptNumber).ToArray());

        // Nothing more happens by itself.
        await MakeDueAsync(factory, queued.Id);
        Assert.Equal(0, (await ProcessAllAsync(factory)).Claimed);
    }

    [Fact]
    public async Task APermanentRefusal_IsGivenUpAtOnce_WithoutWastingAttempts()
    {
        var queued = await QueueOneSmsAsync();
        factory.Sms.Then(SendResult.Rejected("The number does not exist"));

        var run = await ProcessAllAsync(factory);
        var message = await ReloadAsync(queued.Id);

        Assert.Equal(1, run.Failed);
        Assert.Equal(OutboxStatus.DeadLetter, message.Status);
        Assert.Equal(1, message.AttemptCount);
        Assert.Equal("The number does not exist", message.LastError);
    }

    [Fact]
    public async Task AProviderThatThrows_NeverStopsTheWorker_AndTheMessageIsRetried()
    {
        var queued = await QueueOneSmsAsync();
        factory.Sms.ThrowOnce(new InvalidOperationException("boom with a secret 98765"));

        var run = await ProcessAllAsync(factory);
        var message = await ReloadAsync(queued.Id);

        Assert.Equal(1, run.Retrying);
        Assert.Equal(OutboxStatus.Failed, message.Status);
        Assert.Equal(1, message.AttemptCount);
        Assert.Contains("InvalidOperationException", message.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("98765", message.LastError, StringComparison.Ordinal);

        await MakeDueAsync(factory, queued.Id);
        await ProcessAllAsync(factory);
        Assert.Equal(OutboxStatus.Submitted, (await ReloadAsync(queued.Id)).Status);
    }

    [Fact]
    public async Task ANetworkFailure_IsTreatedAsTemporary()
    {
        var queued = await QueueOneSmsAsync();
        factory.Sms.ThrowOnce(new HttpRequestException("connection refused"));

        await ProcessAllAsync(factory);
        var message = await ReloadAsync(queued.Id);

        Assert.Equal(OutboxStatus.Failed, message.Status);
        Assert.Contains("could not be reached", message.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageALostWorkerLeftLocked_IsPickedUpAgainAfterTheLockExpires()
    {
        var queued = await QueueOneSmsAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
            var message = await db.NotificationOutbox.SingleAsync(m => m.Id == queued.Id);
            message.BeginProcessing(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(3));
            db.Entry(message).Property(m => m.LockedUntilUtc).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-7);
            await db.SaveChangesAsync();
        }

        var run = await ProcessAllAsync(factory);
        var recovered = await ReloadAsync(queued.Id);

        Assert.Equal(1, run.Submitted);
        Assert.Equal(OutboxStatus.Submitted, recovered.Status);
        Assert.Equal(2, recovered.AttemptCount);
    }

    [Fact]
    public async Task AMessageThatIsStillLocked_IsLeftAlone()
    {
        var queued = await QueueOneSmsAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
            var message = await db.NotificationOutbox.SingleAsync(m => m.Id == queued.Id);
            message.BeginProcessing(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30));
            await db.SaveChangesAsync();
        }

        var run = await ProcessAllAsync(factory);

        Assert.Equal(0, run.Claimed);
        Assert.Equal(OutboxStatus.Processing, (await ReloadAsync(queued.Id)).Status);

        // Put it back so later tests are not affected.
        await using var scope2 = factory.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var again = await db2.NotificationOutbox.SingleAsync(m => m.Id == queued.Id);
        db2.Entry(again).Property(m => m.LockedUntilUtc).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db2.SaveChangesAsync();
        await ProcessAllAsync(factory);
    }

    [Fact]
    public async Task AnAdministrator_CanGiveADeadMessageAnotherChance()
    {
        var queued = await QueueOneSmsAsync();
        factory.Sms.Then(SendResult.Rejected("Account suspended"));
        await ProcessAllAsync(factory);
        Assert.Equal(OutboxStatus.DeadLetter, (await ReloadAsync(queued.Id)).Status);
        var admin = await AdminAsync(factory);
        var summaryBefore = await admin.GetFromJsonAsync<NotificationSummaryResponse>(new Uri("/api/v1/notifications/summary", UriKind.Relative));
        Assert.True(summaryBefore!.DeadLetters >= 1);

        var requeue = await admin.PostAsJsonAsync(
            new Uri($"/api/v1/notifications/messages/{queued.Id}/requeue", UriKind.Relative), new RequeueNotificationRequest("Account is active again"));

        Assert.True(requeue.IsSuccessStatusCode, await requeue.Content.ReadAsStringAsync());
        var response = await requeue.Content.ReadFromJsonAsync<NotificationMessageResponse>();
        Assert.Equal("Pending", response!.Status);
        Assert.Equal(0, response.AttemptCount);

        await ProcessAllAsync(factory);
        var sent = await ReloadAsync(queued.Id);
        Assert.Equal(OutboxStatus.Submitted, sent.Status);
        Assert.Equal(1, sent.AttemptCount);

        // The earlier failed attempt stays in the history.
        var logs = await LogsAsync(factory, queued.Id);
        Assert.Equal([NotificationLogStatus.Failed, NotificationLogStatus.Submitted], logs.Select(l => l.Status).ToArray());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "NotificationRequeued" && a.EntityId == queued.Id.ToString()));
    }

    [Fact]
    public async Task AMessageThatWasAlreadyAccepted_CannotBeRequeued_SoItCannotBeSentTwice()
    {
        var queued = await QueueOneSmsAsync();
        await ProcessAllAsync(factory);
        var admin = await AdminAsync(factory);

        var requeue = await admin.PostAsJsonAsync(
            new Uri($"/api/v1/notifications/messages/{queued.Id}/requeue", UriKind.Relative), new RequeueNotificationRequest());

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, requeue.StatusCode);
        Assert.Equal(OutboxStatus.Submitted, (await ReloadAsync(queued.Id)).Status);
    }

    [Fact]
    public async Task AWaitingMessage_CanBeCancelled_AndIsThenNeverSent()
    {
        var queued = await QueueOneSmsAsync();
        var admin = await AdminAsync(factory);

        var cancel = await admin.PostAsJsonAsync(
            new Uri($"/api/v1/notifications/messages/{queued.Id}/cancel", UriKind.Relative), new RequeueNotificationRequest("Wrong number on the booking"));
        var run = await ProcessAllAsync(factory);

        Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
        Assert.Equal(0, run.Claimed);
        var message = await ReloadAsync(queued.Id);
        Assert.Equal(OutboxStatus.Cancelled, message.Status);
        Assert.Equal("Wrong number on the booking", message.SkipReason);
    }

    [Fact]
    public async Task TheProviderIsGivenTheNumberWithTheCountryCode_AndTheIdempotencyKey()
    {
        var queued = await QueueOneSmsAsync();

        await ProcessAllAsync(factory);

        var outbound = factory.Sms.Sent.Last(m => m.IdempotencyKey == queued.Id.ToString("N"));
        Assert.Equal("977" + queued.Recipient, outbound.To);
        Assert.Equal(NotificationTemplateKeys.ParcelBooked, outbound.TemplateKey);
        Assert.Equal("en", outbound.Language);
        Assert.Contains("Afno Parcel", outbound.Body, StringComparison.Ordinal);
    }
}
