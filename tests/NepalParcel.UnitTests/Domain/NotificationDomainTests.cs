using NepalParcel.Domain.Common;
using NepalParcel.Domain.Integrations;
using NepalParcel.Domain.Notifications;

namespace NepalParcel.UnitTests.Domain;

/// <summary>
/// The rules of the notification outbox: what we did with a message (its status) is kept apart from what the provider
/// reported (its delivery status), and a message the provider only accepted is never "delivered".
/// </summary>
public sealed class NotificationDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);

    private static NotificationOutboxMessage Queued(int maxAttempts = 5) =>
        NotificationOutboxMessage.EnqueueForParcel(
            NotificationChannel.WhatsApp, NotificationAudience.Receiver, "9851234567", NotificationTemplateKeys.ParcelShipped, "{}", Now,
            Guid.NewGuid(), null, "event-1", maxAttempts);

    private static NotificationOutboxMessage Submitted()
    {
        var message = Queued();
        message.BeginProcessing(Now, TimeSpan.FromMinutes(3));
        message.MarkSubmitted(Now, "MetaWhatsApp", "wamid.ABC", "Afno Parcel: parcel NP-1 has been shipped.");
        return message;
    }

    // ---- accepted is not delivered -----------------------------------------------------------------------------------

    [Fact]
    public void AMessageTheProviderAccepted_IsSubmitted_NotDelivered()
    {
        var message = Submitted();

        Assert.Equal(OutboxStatus.Submitted, message.Status);
        Assert.Equal(DeliveryStatus.None, message.DeliveryStatus);
        Assert.Equal("wamid.ABC", message.ProviderMessageId);
        Assert.Equal("MetaWhatsApp", message.Provider);
        Assert.Equal(Now, message.SubmittedAtUtc);
        Assert.Null(message.SentAtUtc);
        Assert.Null(message.DeliveredAtUtc);
        Assert.Null(message.ReadAtUtc);
    }

    [Fact]
    public void OnlyProviderReports_ChangeTheDeliveryStatus()
    {
        var message = Submitted();

        Assert.True(message.ApplyProviderStatus(DeliveryStatus.Sent, Now.AddSeconds(5)));
        Assert.Equal(DeliveryStatus.Sent, message.DeliveryStatus);
        Assert.Null(message.DeliveredAtUtc);

        Assert.True(message.ApplyProviderStatus(DeliveryStatus.Delivered, Now.AddSeconds(9)));
        Assert.Equal(DeliveryStatus.Delivered, message.DeliveryStatus);
        Assert.Equal(Now.AddSeconds(9), message.DeliveredAtUtc);

        Assert.True(message.ApplyProviderStatus(DeliveryStatus.Read, Now.AddMinutes(2)));
        Assert.Equal(DeliveryStatus.Read, message.DeliveryStatus);
        Assert.Equal(Now.AddMinutes(2), message.ReadAtUtc);

        // Our own status never moved: the provider's reports are a separate thing.
        Assert.Equal(OutboxStatus.Submitted, message.Status);
    }

    [Fact]
    public void ProviderReports_ArriveLateTwiceAndOutOfOrder_AndTheStateOnlyMovesForward()
    {
        var message = Submitted();
        message.ApplyProviderStatus(DeliveryStatus.Delivered, Now.AddSeconds(9));

        // "sent" arrives after "delivered": it is old news.
        Assert.False(message.ApplyProviderStatus(DeliveryStatus.Sent, Now.AddSeconds(6)));
        Assert.Equal(DeliveryStatus.Delivered, message.DeliveryStatus);
        Assert.Null(message.SentAtUtc);

        // The same report twice changes nothing.
        Assert.False(message.ApplyProviderStatus(DeliveryStatus.Delivered, Now.AddSeconds(9)));

        // A failure never undoes a delivery.
        Assert.False(message.ApplyProviderStatus(DeliveryStatus.Failed, Now.AddSeconds(30), "late failure"));
        Assert.Equal(DeliveryStatus.Delivered, message.DeliveryStatus);
        Assert.Null(message.FailedAtUtc);
        Assert.Null(message.DeliveryError);
    }

    [Fact]
    public void ReadImpliesDelivered_EvenWhenTheDeliveredReportNeverArrives()
    {
        var message = Submitted();

        message.ApplyProviderStatus(DeliveryStatus.Read, Now.AddMinutes(1));

        Assert.Equal(DeliveryStatus.Read, message.DeliveryStatus);
        Assert.Equal(Now.AddMinutes(1), message.DeliveredAtUtc);
    }

    [Fact]
    public void AFailureReport_RecordsWhyAndCanStillBeFollowedByADelivery()
    {
        var message = Submitted();

        Assert.True(message.ApplyProviderStatus(DeliveryStatus.Failed, Now.AddSeconds(4), "[131026] Message undeliverable"));
        Assert.Equal(DeliveryStatus.Failed, message.DeliveryStatus);
        Assert.Equal("[131026] Message undeliverable", message.DeliveryError);
        Assert.Equal(Now.AddSeconds(4), message.FailedAtUtc);

        // The phone came back online and the provider delivered after all.
        Assert.True(message.ApplyProviderStatus(DeliveryStatus.Delivered, Now.AddMinutes(10)));
        Assert.Equal(DeliveryStatus.Delivered, message.DeliveryStatus);
        Assert.Null(message.DeliveryError);
    }

    [Fact]
    public void AFailureReportWithoutAReason_StillSaysThatItFailed()
    {
        var message = Submitted();

        message.ApplyProviderStatus(DeliveryStatus.Failed, Now);

        Assert.False(string.IsNullOrWhiteSpace(message.DeliveryError));
    }

    [Theory]
    [InlineData(OutboxStatus.Pending)]
    [InlineData(OutboxStatus.Processing)]
    [InlineData(OutboxStatus.DeadLetter)]
    [InlineData(OutboxStatus.Skipped)]
    public void AMessageThatWasNeverAccepted_CannotReceiveDeliveryReports(OutboxStatus state)
    {
        var message = Queued(maxAttempts: 1);
        switch (state)
        {
            case OutboxStatus.Processing:
                message.BeginProcessing(Now, TimeSpan.FromMinutes(1));
                break;
            case OutboxStatus.DeadLetter:
                message.BeginProcessing(Now, TimeSpan.FromMinutes(1));
                message.MarkDeadLetter(Now, "rejected");
                break;
            case OutboxStatus.Skipped:
                message.Skip(Now, "no consent");
                break;
        }

        Assert.Equal(state, message.Status);
        Assert.Throws<DomainException>(() => message.ApplyProviderStatus(DeliveryStatus.Delivered, Now));
        Assert.Equal(DeliveryStatus.None, message.DeliveryStatus);
    }

    [Fact]
    public void AProviderCannotReportNone()
    {
        Assert.Throws<DomainException>(() => Submitted().ApplyProviderStatus(DeliveryStatus.None, Now));
    }

    // ---- retries -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ANewMessage_IsDueAtOnce()
    {
        var message = Queued();

        Assert.Equal(OutboxStatus.Pending, message.Status);
        Assert.True(message.IsDue(Now));
        Assert.Equal(0, message.AttemptCount);
    }

    [Fact]
    public void AFailedAttempt_WaitsForTheRetryDelay_ThenIsDueAgain()
    {
        var message = Queued();
        message.BeginProcessing(Now, TimeSpan.FromMinutes(3));
        message.MarkFailed(Now, "timeout", TimeSpan.FromMinutes(2));

        Assert.Equal(OutboxStatus.Failed, message.Status);
        Assert.Equal(1, message.AttemptCount);
        Assert.False(message.IsDue(Now.AddMinutes(1)));
        Assert.True(message.IsDue(Now.AddMinutes(2)));
        Assert.Equal("timeout", message.LastError);
    }

    [Fact]
    public void WhenEveryAttemptHasFailed_TheMessageIsDeadLettered()
    {
        var message = Queued(maxAttempts: 3);
        for (var i = 0; i < 3; i++)
        {
            message.BeginProcessing(Now.AddMinutes(i * 10), TimeSpan.FromMinutes(3));
            message.MarkFailed(Now.AddMinutes(i * 10), $"attempt {i + 1} failed", TimeSpan.FromMinutes(5));
        }

        Assert.Equal(OutboxStatus.DeadLetter, message.Status);
        Assert.Equal(3, message.AttemptCount);
        Assert.Equal("attempt 3 failed", message.LastError);
        Assert.False(message.IsDue(Now.AddDays(1)));
    }

    [Fact]
    public void AMessageThatAWorkerLeftLocked_BecomesDueWhenTheLockExpires()
    {
        var message = Queued();
        message.BeginProcessing(Now, TimeSpan.FromMinutes(3));

        Assert.False(message.IsDue(Now.AddMinutes(2)));
        Assert.True(message.IsDue(Now.AddMinutes(3)));
    }

    [Fact]
    public void AMessageThatIsNotDue_CannotBeStarted()
    {
        var message = Submitted();

        Assert.Throws<DomainException>(() => message.BeginProcessing(Now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ARejectedMessage_IsDeadLetteredAtOnce_WhateverAttemptsAreLeft()
    {
        var message = Queued(maxAttempts: 5);
        message.BeginProcessing(Now, TimeSpan.FromMinutes(1));

        message.MarkDeadLetter(Now, "invalid number");

        Assert.Equal(OutboxStatus.DeadLetter, message.Status);
        Assert.Equal("invalid number", message.LastError);
    }

    [Fact]
    public void RequeueingADeadLetter_GivesItAFreshSetOfAttempts()
    {
        var message = Queued(maxAttempts: 1);
        message.BeginProcessing(Now, TimeSpan.FromMinutes(1));
        message.MarkFailed(Now, "down", TimeSpan.FromMinutes(1));
        Assert.Equal(OutboxStatus.DeadLetter, message.Status);

        message.Requeue(Now.AddHours(1));

        Assert.Equal(OutboxStatus.Pending, message.Status);
        Assert.Equal(0, message.AttemptCount);
        Assert.True(message.IsDue(Now.AddHours(1)));
        Assert.Null(message.ProcessedAtUtc);
    }

    [Fact]
    public void AMessageThatWasAccepted_CannotBeRequeuedOrCancelled()
    {
        var message = Submitted();

        Assert.Throws<DomainException>(() => message.Requeue(Now));
        Assert.Throws<DomainException>(() => message.Cancel(Now, "oops"));
    }

    [Fact]
    public void ACancelledMessage_IsNeverSent()
    {
        var message = Queued();

        message.Cancel(Now, "wrong number");

        Assert.Equal(OutboxStatus.Cancelled, message.Status);
        Assert.Equal("wrong number", message.SkipReason);
        Assert.False(message.IsDue(Now.AddDays(1)));
    }

    // ---- skipped -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ASkippedMessage_KeepsTheReason_AndIsNeverDue()
    {
        var message = NotificationOutboxMessage.CreateSkipped(
            NotificationChannel.Sms, NotificationAudience.Sender, "9841234567", NotificationTemplateKeys.ParcelBooked, Now,
            "The sender has not agreed to receive SMS messages.", Guid.NewGuid(), Guid.NewGuid(), "event-1", 5);

        Assert.Equal(OutboxStatus.Skipped, message.Status);
        Assert.Equal("The sender has not agreed to receive SMS messages.", message.SkipReason);
        Assert.False(message.IsDue(Now.AddDays(1)));
        Assert.Equal(0, message.AttemptCount);
    }

    [Fact]
    public void AMessageCanBeSkippedWhileItIsBeingProcessed_ForExampleWhenConsentWasWithdrawn()
    {
        var message = Queued();
        message.BeginProcessing(Now, TimeSpan.FromMinutes(1));

        message.Skip(Now, "The sender no longer agrees to receive WhatsApp messages.");

        Assert.Equal(OutboxStatus.Skipped, message.Status);
        Assert.Null(message.LockedUntilUtc);
    }

    [Fact]
    public void ASubmittedMessage_CannotBeSkipped()
    {
        Assert.Throws<DomainException>(() => Submitted().Skip(Now, "too late"));
    }

    // ---- one-time codes ----------------------------------------------------------------------------------------------

    [Fact]
    public void OneTimeCodes_AreRecognisedByTheirKey_AndTheirCodeCanBeErasedFromThePayload()
    {
        Assert.True(NotificationTemplateKeys.IsOneTimeCode(NotificationTemplateKeys.TrackingOtp));
        Assert.True(NotificationTemplateKeys.IsOneTimeCode(NotificationTemplateKeys.DeliveryOtp));
        Assert.All(NotificationTemplateKeys.ParcelEvents, key => Assert.False(NotificationTemplateKeys.IsOneTimeCode(key)));

        var message = NotificationOutboxMessage.Enqueue(NotificationChannel.Sms, "9851234567", NotificationTemplateKeys.TrackingOtp, "{\"code\":\"123456\"}", Now);
        message.RedactPayload();

        Assert.Equal("{}", message.PayloadJson);
    }

    [Fact]
    public void TheLegacyEnqueue_StillQueuesToTheReceiverWithoutADedupeKey()
    {
        var message = NotificationOutboxMessage.Enqueue(NotificationChannel.Sms, "9851234567", NotificationTemplateKeys.DeliveryOtp, "{}", Now, Guid.NewGuid());

        Assert.Equal(NotificationAudience.Receiver, message.Audience);
        Assert.Null(message.DedupeKey);
        Assert.Equal(NotificationOutboxMessage.DefaultMaxAttempts, message.MaxAttempts);
    }

    [Fact]
    public void AMessageNeedsAChannelAudienceAndAtLeastOneAttempt()
    {
        Assert.Throws<DomainException>(() => NotificationOutboxMessage.Enqueue((NotificationChannel)99, "9851234567", "k", "{}", Now));
        Assert.Throws<DomainException>(() => NotificationOutboxMessage.Enqueue(NotificationChannel.Sms, "9851234567", "k", "{}", Now, maxAttempts: 0));
        Assert.Throws<DomainException>(() => NotificationOutboxMessage.Enqueue(NotificationChannel.Sms, " ", "k", "{}", Now));
        Assert.Throws<DomainException>(() => NotificationOutboxMessage.EnqueueForParcel(
            NotificationChannel.Sms, (NotificationAudience)9, "9851234567", "k", "{}", Now, Guid.NewGuid(), null, "e", 3));
    }

    // ---- attempt log -------------------------------------------------------------------------------------------------

    [Fact]
    public void ALogRow_RecordsTheAttemptOrTheProvidersReport_WithoutTheFullNumber()
    {
        var attempt = NotificationLog.Create(
            NotificationChannel.WhatsApp, "98******67", "MetaWhatsApp", NotificationLogStatus.Submitted, Now, Guid.NewGuid(), "wamid.ABC", attemptNumber: 2);
        var report = NotificationLog.Create(
            NotificationChannel.WhatsApp, "98******67", "MetaWhatsApp", NotificationLogStatus.Delivered, Now, attempt.OutboxMessageId, "wamid.ABC", occurredAtUtc: Now.AddSeconds(8));

        Assert.Equal(2, attempt.AttemptNumber);
        Assert.Null(attempt.OccurredAtUtc);
        Assert.Equal(Now.AddSeconds(8), report.OccurredAtUtc);
        Assert.Null(report.AttemptNumber);
        Assert.Equal("98******67", attempt.RecipientMasked);
    }

    // ---- templates, opt-outs, webhooks --------------------------------------------------------------------------------

    [Fact]
    public void TemplatesExistForSmsAndWhatsAppOnly()
    {
        var sms = NotificationTemplate.Create("parcel-booked", NotificationChannel.Sms, "en", "Booked {tracking}", null, Now);
        Assert.True(sms.IsActive);

        Assert.Throws<DomainException>(() => NotificationTemplate.Create("parcel-booked", NotificationChannel.Email, "en", "x", null, Now));
        Assert.Throws<DomainException>(() => NotificationTemplate.Create("parcel-booked", NotificationChannel.Sms, "en", " ", null, Now));
    }

    [Fact]
    public void UpdatingATemplate_ChangesTheTextAndRecordsWho()
    {
        var template = NotificationTemplate.Create("parcel-booked", NotificationChannel.WhatsApp, "en", "Booked {tracking}", "afno_parcel_booked", Now);
        var admin = Guid.NewGuid();

        template.Update("Your parcel {tracking} is booked", "afno_booked_v2", false, Now.AddDays(1), admin);

        Assert.Equal("Your parcel {tracking} is booked", template.Body);
        Assert.Equal("afno_booked_v2", template.ProviderTemplateName);
        Assert.False(template.IsActive);
        Assert.Equal(admin, template.UpdatedByUserId);
        Assert.Equal(Now.AddDays(1), template.UpdatedAtUtc);
    }

    [Fact]
    public void AnOptOut_NormalisesTheNumber_AppliesToOneChannel_AndIsLiftedWithoutBeingDeleted()
    {
        var optOut = NotificationOptOut.Create("+977 984-1234567", NotificationChannel.WhatsApp, OptOutSource.InboundMessage, Now);

        Assert.Equal("9841234567", optOut.PhoneNumber);
        Assert.True(optOut.IsActive);

        optOut.Remove(Now.AddDays(1));

        Assert.False(optOut.IsActive);
        Assert.Equal(Now.AddDays(1), optOut.RemovedAtUtc);
        Assert.Throws<DomainException>(() => optOut.Remove(Now.AddDays(2)));
    }

    [Fact]
    public void AnOptOut_NeedsARealMobileChannel()
    {
        Assert.Throws<DomainException>(() => NotificationOptOut.Create("9841234567", NotificationChannel.Email, OptOutSource.Staff, Now));
        Assert.Throws<DomainException>(() => NotificationOptOut.Create("12345", NotificationChannel.Sms, OptOutSource.Staff, Now));
    }

    [Fact]
    public void AWebhookThatCouldNotBeAppliedYet_StaysReceived_SoItIsTriedAgain()
    {
        var received = WebhookEvent.Receive("MetaWhatsApp", "wamid.ABC:delivered", "status:delivered", "{}", signatureValid: true, Now);

        received.NoteAttempt("No submitted message has this provider id yet.");

        Assert.Equal(WebhookStatus.Received, received.Status);
        Assert.Equal(1, received.AttemptCount);

        received.MarkProcessed(Now.AddMinutes(1));

        Assert.Equal(WebhookStatus.Processed, received.Status);
        Assert.Equal(2, received.AttemptCount);
        Assert.Null(received.Error);
    }
}
