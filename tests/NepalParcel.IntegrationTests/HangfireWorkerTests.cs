using System.Net.Http.Json;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Notifications;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;
using static NepalParcel.IntegrationTests.Infrastructure.NotificationTestSupport;

namespace NepalParcel.IntegrationTests;

/// <summary>The API with a real Hangfire worker (in-memory storage), so the wiring itself is exercised and not only the processor.</summary>
public sealed class HangfireWorkerFactory : NotificationApiFactory
{
    public HangfireWorkerFactory() => Settings["Hangfire:Mode"] = "InMemory";
}

/// <summary>
/// Nobody calls the processor here: booking a parcel is enough. The save that queues a message tells the worker, the worker
/// sweeps the outbox, and the message ends up submitted to the (mock) provider.
/// </summary>
public sealed class HangfireWorkerTests(HangfireWorkerFactory factory) : IClassFixture<HangfireWorkerFactory>
{
    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        T value;
        do
        {
            value = await read();
            if (done(value))
            {
                return value;
            }

            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);

        return value;
    }

    [Fact]
    public async Task BookingAParcel_IsEnoughForTheBackgroundWorkerToSendItsMessages()
    {
        var helpers = new BookingTestHelpers(factory);
        var branch = await helpers.CreateBranchAsync();
        var counter = await helpers.StaffAsync(Roles.BookingStaff);
        var request = NewBooking(branch, senderName: NewName("Sender"), receiverPhone: NewPhone()) with { NotifyBySms = true, NotifyByWhatsApp = true };

        var parcel = await BookingTestHelpers.BookAsync(counter, request);

        var messages = await WaitForAsync(
            () => MessagesAsync(factory, parcel.Id),
            all => all.Count == 4 && all.All(m => m.Status == OutboxStatus.Submitted));
        Assert.Equal(4, messages.Count);
        Assert.All(messages, m => Assert.Equal(OutboxStatus.Submitted, m.Status));
        Assert.All(messages, m => Assert.Equal(DeliveryStatus.None, m.DeliveryStatus));
        Assert.All(messages, m => Assert.False(string.IsNullOrWhiteSpace(m.ProviderMessageId)));
    }

    [Fact]
    public async Task ARollbackSendsNothing_BecauseTheMessageIsSavedWithTheChangeOrNotAtAll()
    {
        var helpers = new BookingTestHelpers(factory);
        var branch = await helpers.CreateBranchAsync();
        var counter = await helpers.StaffAsync(Roles.BookingStaff);
        var parcel = await BookingTestHelpers.BookAsync(counter, NewBooking(branch, senderName: NewName("Sender"), receiverPhone: NewPhone()));
        await WaitForAsync(() => MessagesAsync(factory, parcel.Id), all => all.Any(m => m.Status == OutboxStatus.Submitted));
        var before = (await MessagesAsync(factory, parcel.Id)).Count;

        // A status change the workflow refuses is rolled back, so no message is queued for it.
        var refused = await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("Delivered", "Skipping every step"));
        await Task.Delay(1500);

        Assert.False(refused.IsSuccessStatusCode);
        Assert.Equal(before, (await MessagesAsync(factory, parcel.Id)).Count);
    }

    [Fact]
    public void TheRecurringSweep_IsScheduled_SoALostHintDelaysAMessageButNeverLosesIt()
    {
        var storage = factory.Services.GetRequiredService<JobStorage>();

        using var connection = storage.GetConnection();
        var job = Assert.Single(Hangfire.Storage.StorageConnectionExtensions.GetRecurringJobs(connection), j => j.Id == "notification-outbox");

        Assert.Equal("* * * * *", job.Cron);
        Assert.Contains(nameof(NepalParcel.API.Extensions.NotificationJobs.SweepAsync), job.Job.Method.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSweep_AlsoPicksUpMessagesNobodyAskedItAbout()
    {
        var helpers = new BookingTestHelpers(factory);
        var branch = await helpers.CreateBranchAsync();
        var counter = await helpers.StaffAsync(Roles.BookingStaff);
        var parcel = await BookingTestHelpers.BookAsync(counter, NewBooking(branch, senderName: NewName("Sender"), receiverPhone: NewPhone()));

        // Run the job the way the scheduler would.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NepalParcel.API.Extensions.NotificationJobs>().SweepAsync(CancellationToken.None);
        }

        var messages = await WaitForAsync(() => MessagesAsync(factory, parcel.Id), all => all.Any(m => m.Status == OutboxStatus.Submitted));
        Assert.Contains(messages, m => m.Status == OutboxStatus.Submitted);
        Assert.Equal(DeliveryStatus.None, messages.First(m => m.Status == OutboxStatus.Submitted).DeliveryStatus);
    }
}
