using NepalParcel.Domain.Common;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Payments;
using NepalParcel.Domain.Pricing;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.UnitTests.Domain;

public sealed class ModuleEntityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Roles_ContainExactlyTheSevenRequiredRoles()
    {
        Assert.Equal(
            ["SuperAdmin", "BranchManager", "BookingStaff", "WarehouseStaff", "DeliveryAgent", "Accountant", "Customer"],
            Roles.All);
        Assert.DoesNotContain(Roles.SuperAdmin, Roles.AssignableStaff);
        Assert.DoesNotContain(Roles.Customer, Roles.AssignableStaff);
    }

    [Fact]
    public void Customer_WalkIn_HasNoUser_AndNormalizesContact()
    {
        var customer = Customer.Create(" Ram Bahadur ", "+977-9841234567", "RAM@Example.com");

        Assert.True(customer.IsWalkIn);
        Assert.Equal("Ram Bahadur", customer.FullName);
        Assert.Equal("9841234567", customer.PhoneNumber);
        Assert.Equal("ram@example.com", customer.Email);
    }

    [Fact]
    public void Customer_Business_RequiresCompanyName_AndValidPhone()
    {
        Assert.Throws<DomainException>(() => Customer.Create("A", "9841234567", type: CustomerType.Business));
        Assert.Throws<DomainException>(() => Customer.Create("A", "123"));
    }

    [Fact]
    public void RefreshToken_Rotation_RevokesOld_AndKeepsFamily()
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var first = RefreshToken.Issue(Guid.NewGuid(), hashA, Now, TimeSpan.FromDays(7), "10.0.0.1");

        var second = first.Rotate(hashB, Now.AddMinutes(5), TimeSpan.FromDays(7), "10.0.0.1");

        Assert.True(first.IsRevoked);
        Assert.Equal("Rotated", first.RevokedReason);
        Assert.Equal(second.Id, first.ReplacedByTokenId);
        Assert.Equal(first.FamilyId, second.FamilyId);
        Assert.True(second.IsActive(Now.AddMinutes(6)));
        Assert.False(second.IsActive(Now.AddDays(8)));
    }

    [Fact]
    public void RefreshToken_RejectsMalformedHash()
    {
        Assert.Throws<DomainException>(() => RefreshToken.Issue(Guid.NewGuid(), "short", Now, TimeSpan.FromDays(1), null));
    }

    [Fact]
    public void DeliveryAssignment_FollowsLifecycle()
    {
        var assignment = DeliveryAssignment.Assign(Guid.NewGuid(), Guid.NewGuid(), AssignmentType.Delivery, Now);

        assignment.Accept();
        assignment.Start(Now.AddMinutes(10));
        assignment.Fail(Now.AddHours(1), "Recipient not available");

        Assert.Equal(AssignmentStatus.Failed, assignment.Status);
        Assert.False(assignment.IsOpen);
        Assert.Throws<DomainException>(() => assignment.Complete(Now.AddHours(2)));
    }

    [Fact]
    public void DeliveryAssignment_CannotBeCancelledOnceCompleted()
    {
        var assignment = DeliveryAssignment.Assign(Guid.NewGuid(), Guid.NewGuid(), AssignmentType.Pickup, Now);
        assignment.Start(Now);
        assignment.Complete(Now.AddMinutes(30));

        Assert.Throws<DomainException>(() => assignment.Cancel(Now.AddHours(1)));
    }

    [Fact]
    public void Payment_CanOnlyBePaidOnce_AndAmountIsRounded()
    {
        var payment = Payment.Create(Guid.NewGuid(), 199.999m, PaymentMethod.Esewa);
        payment.MarkPaid(Now, "ESW-1");

        Assert.Equal(200.00m, payment.Amount);
        Assert.Equal("NPR", payment.Currency);
        Assert.Throws<DomainException>(() => payment.MarkPaid(Now));
        Assert.Throws<DomainException>(() => Payment.Create(Guid.NewGuid(), 0m, PaymentMethod.Cash));
    }

    [Fact]
    public void PaymentTransaction_CannotChangeAfterCompletion()
    {
        var transaction = PaymentTransaction.Create(Guid.NewGuid(), TransactionType.Charge, "esewa", 500m, Now, "REF1");
        transaction.Succeed(Now.AddSeconds(5));

        Assert.Equal(TransactionStatus.Succeeded, transaction.Status);
        Assert.Throws<DomainException>(() => transaction.Fail(Now, "late failure"));
    }

    [Fact]
    public void CodCollection_AttachedToSettlementOnce_AndTracksVariance()
    {
        var collection = CodCollection.Record(Guid.NewGuid(), Guid.NewGuid(), 1000m, 950m, Now);

        Assert.Equal(-50m, collection.Variance);
        collection.AttachToSettlement(Guid.NewGuid());
        Assert.Equal(CodCollectionStatus.Collected, collection.Status); // settled only once the cash has been counted
        Assert.True(collection.IsInSettlement);
        Assert.Throws<DomainException>(() => collection.AttachToSettlement(Guid.NewGuid()));
        collection.MarkSettled();
        Assert.Equal(CodCollectionStatus.Settled, collection.Status);
    }

    [Fact]
    public void CodSettlement_ProgressesDraftSubmittedVerified()
    {
        var settlement = CodSettlement.Create("set-0001", Guid.NewGuid(), 12500m, 4);

        Assert.Equal("SET-0001", settlement.ReferenceNumber);
        Assert.Throws<DomainException>(() => settlement.Verify(Now, Guid.NewGuid(), 12500m));
        settlement.Submit(Now);
        settlement.Verify(Now.AddHours(1), Guid.NewGuid(), 12500m);
        Assert.Equal(SettlementStatus.Verified, settlement.Status);
        Assert.Equal(0m, settlement.Variance);
    }

    [Fact]
    public void Outbox_RetriesThenDeadLetters()
    {
        var message = NotificationOutboxMessage.Enqueue(NotificationChannel.Sms, "9841234567", "parcel-booked", "{}", Now, maxAttempts: 2);

        message.BeginProcessing(Now, TimeSpan.FromMinutes(1));
        message.MarkFailed(Now, "provider timeout", TimeSpan.FromMinutes(5));
        Assert.Equal(OutboxStatus.Failed, message.Status);
        Assert.Equal(Now.AddMinutes(5), message.NextAttemptAtUtc);

        message.BeginProcessing(Now.AddMinutes(5), TimeSpan.FromMinutes(1));
        message.MarkFailed(Now.AddMinutes(5), "provider timeout", TimeSpan.FromMinutes(5));
        Assert.Equal(OutboxStatus.DeadLetter, message.Status);
    }

    [Fact]
    public void ConsentRecords_AreImmutableDecisions()
    {
        var granted = CustomerConsent.Record(Guid.NewGuid(), ConsentType.SmsNotifications, true, "2026-01", ConsentSource.Counter, Now);
        var withdrawn = CustomerConsent.Record(granted.CustomerId, ConsentType.SmsNotifications, false, "2026-01", ConsentSource.Web, Now.AddDays(1));

        Assert.True(granted.IsGranted);
        Assert.False(withdrawn.IsGranted);
        Assert.NotEqual(granted.Id, withdrawn.Id);
    }

    [Fact]
    public void DeliveryProof_RequiresEvidenceForItsType()
    {
        Assert.Throws<DomainException>(() => DeliveryProof.Create(Guid.NewGuid(), ProofType.Signature, Now));
        Assert.Throws<DomainException>(() => DeliveryProof.Create(Guid.NewGuid(), ProofType.Otp, Now, otpVerified: false));
        Assert.NotNull(DeliveryProof.Create(Guid.NewGuid(), ProofType.Otp, Now, otpVerified: true));
    }

    [Fact]
    public void PricingRule_ValidatesWeightBand_AndCodFee()
    {
        Assert.Throws<DomainException>(() => PricingRule.Create("Bad", ServiceType.Standard, 5m, 5m, 100m, 10m, 1m, Now));
        Assert.Throws<DomainException>(() => PricingRule.Create("Bad", ServiceType.Standard, 0m, 5m, 100m, 10m, 101m, Now));

        var rule = PricingRule.Create("Valley standard", ServiceType.Standard, 0m, 5m, 150.456m, 20m, 1.5m, Now);
        Assert.Equal(150.46m, rule.BasePrice);
    }

    [Fact]
    public void ServiceArea_NormalizesCode_AndBoundsDeliveryDays()
    {
        var area = ServiceArea.Create("ktm-valley", "Kathmandu Valley", Province.Bagmati, "Kathmandu", 1);

        Assert.Equal("KTM-VALLEY", area.Code);
        Assert.Throws<DomainException>(() => ServiceArea.Create("X", "X", Province.Koshi, "Morang", 31));
    }
}
