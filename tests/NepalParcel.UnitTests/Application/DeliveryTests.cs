using Microsoft.Extensions.Logging.Abstractions;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Delivery;
using NepalParcel.Application.Network;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.UnitTests.Domain;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

public sealed class DeliveryAssignmentDomainTests
{
    private static readonly Guid Parcel = Guid.NewGuid();
    private static readonly Guid Agent = Guid.NewGuid();

    private static DeliveryAssignment NewAssignment(int attempt = 1) =>
        DeliveryAssignment.Assign(Parcel, Agent, AssignmentType.Delivery, TestData.Now, Guid.NewGuid(), attempt);

    [Fact]
    public void ANewAssignment_IsAssignedAndOpen()
    {
        var assignment = NewAssignment();

        Assert.Equal(AssignmentStatus.Assigned, assignment.Status);
        Assert.True(assignment.IsOpen);
        Assert.Equal(1, assignment.AttemptNumber);
    }

    [Fact]
    public void ADeliveryRunsAssignedAcceptedInProgressCompleted()
    {
        var assignment = NewAssignment();

        assignment.Accept();
        assignment.Start(TestData.Now.AddHours(1));
        assignment.Complete(TestData.Now.AddHours(2));

        Assert.Equal(AssignmentStatus.Completed, assignment.Status);
        Assert.False(assignment.IsOpen);
        Assert.Equal(TestData.Now.AddHours(1), assignment.StartedAtUtc);
        Assert.Equal(TestData.Now.AddHours(2), assignment.CompletedAtUtc);
    }

    [Fact]
    public void AnAssignmentCannotBeCompletedOrFailed_BeforeItIsStarted()
    {
        Assert.Throws<DomainException>(() => NewAssignment().Complete(TestData.Now));
        Assert.Throws<DomainException>(() => NewAssignment().Fail(TestData.Now, "Receiver not at home"));
    }

    [Fact]
    public void AFailedAttempt_NeedsAReason_AndCannotBeRestartedOrCancelled()
    {
        var assignment = NewAssignment();
        assignment.Start(TestData.Now);

        Assert.Throws<DomainException>(() => assignment.Fail(TestData.Now, "  "));
        Assert.Equal(AssignmentStatus.InProgress, assignment.Status); // a refused call changes nothing
        Assert.Null(assignment.FailureReason);
        assignment.Fail(TestData.Now.AddHours(1), "Receiver not at home");

        Assert.Equal(AssignmentStatus.Failed, assignment.Status);
        Assert.Equal("Receiver not at home", assignment.FailureReason);
        Assert.Throws<DomainException>(() => assignment.Start(TestData.Now));
        Assert.Throws<DomainException>(() => assignment.Cancel(TestData.Now));
        Assert.Throws<DomainException>(() => assignment.Complete(TestData.Now));
    }

    [Fact]
    public void AnOpenAssignmentCanBeCancelled_ButNotTwice()
    {
        var assignment = NewAssignment();

        assignment.Cancel(TestData.Now.AddMinutes(5));

        Assert.Equal(AssignmentStatus.Cancelled, assignment.Status);
        Assert.Throws<DomainException>(() => assignment.Cancel(TestData.Now));
        Assert.Throws<DomainException>(() => assignment.Accept());
    }

    [Fact]
    public void Assign_RejectsMissingIdsUnknownTypesAndAttemptZero()
    {
        Assert.Throws<DomainException>(() => DeliveryAssignment.Assign(Guid.Empty, Agent, AssignmentType.Delivery, TestData.Now));
        Assert.Throws<DomainException>(() => DeliveryAssignment.Assign(Parcel, Guid.Empty, AssignmentType.Delivery, TestData.Now));
        Assert.Throws<DomainException>(() => DeliveryAssignment.Assign(Parcel, Agent, (AssignmentType)42, TestData.Now));
        Assert.Throws<DomainException>(() => DeliveryAssignment.Assign(Parcel, Agent, AssignmentType.Delivery, TestData.Now, null, 0));
    }
}

public sealed class DeliveryAgentDomainTests
{
    private static DeliveryAgent NewAgent() =>
        DeliveryAgent.Create(Guid.NewGuid(), Guid.NewGuid(), "Ram Rider", "9841000111", "Motorbike", "ba 1 pa 1234", "LIC-1");

    [Fact]
    public void AnAgentStartsActiveAndAvailable_WithTheVehicleNumberInCapitals()
    {
        var agent = NewAgent();

        Assert.True(agent.IsActive);
        Assert.True(agent.IsAvailable);
        Assert.Equal("BA 1 PA 1234", agent.VehicleNumber);
    }

    [Fact]
    public void UpdateDetails_ChangesPhoneAndVehicle_AndValidatesThePhone()
    {
        var agent = NewAgent();

        agent.UpdateDetails("9801112233", "Van", "ba 2 cha 7", null);

        Assert.Equal("9801112233", agent.PhoneNumber);
        Assert.Equal("Van", agent.VehicleType);
        Assert.Equal("BA 2 CHA 7", agent.VehicleNumber);
        Assert.Null(agent.LicenseNumber);
        Assert.Throws<DomainException>(() => agent.UpdateDetails("123", null, null, null));
        Assert.Equal("9801112233", agent.PhoneNumber);
    }

    [Fact]
    public void ASwitchedOffAgent_IsNotAvailable_AndCannotBeMadeAvailableUntilActivated()
    {
        var agent = NewAgent();

        agent.Deactivate();

        Assert.False(agent.IsActive);
        Assert.False(agent.IsAvailable);
        Assert.Throws<DomainException>(() => agent.SetAvailability(true));
        agent.Activate();
        Assert.True(agent.IsActive);
        Assert.True(agent.IsAvailable);
    }
}

public sealed class DeliveryOtpDomainTests
{
    private static DeliveryOtp NewOtp() => DeliveryOtp.Create(Guid.NewGuid(), Guid.NewGuid(), "hash", TestData.Now, TimeSpan.FromMinutes(10));

    [Fact]
    public void ACode_IsUsableUntilItExpiresIsUsedOrIsLocked()
    {
        var otp = NewOtp();

        Assert.True(otp.IsUsable(TestData.Now.AddMinutes(5), 5));
        Assert.False(otp.IsUsable(TestData.Now.AddMinutes(10), 5)); // expired
        for (var i = 0; i < 5; i++)
        {
            otp.RegisterFailedAttempt();
        }

        Assert.False(otp.IsUsable(TestData.Now.AddMinutes(1), 5)); // locked
    }

    [Fact]
    public void ACode_CanBeConsumedOnce()
    {
        var otp = NewOtp();

        otp.Consume(TestData.Now.AddMinutes(1));

        Assert.False(otp.IsUsable(TestData.Now.AddMinutes(2), 5));
        Assert.Throws<DomainException>(() => otp.Consume(TestData.Now.AddMinutes(3)));
    }

    [Fact]
    public void Create_RejectsMissingIdsBlankHashesAndNoLifetime()
    {
        Assert.Throws<DomainException>(() => DeliveryOtp.Create(Guid.Empty, Guid.NewGuid(), "h", TestData.Now, TimeSpan.FromMinutes(1)));
        Assert.Throws<DomainException>(() => DeliveryOtp.Create(Guid.NewGuid(), Guid.Empty, "h", TestData.Now, TimeSpan.FromMinutes(1)));
        Assert.Throws<DomainException>(() => DeliveryOtp.Create(Guid.NewGuid(), Guid.NewGuid(), " ", TestData.Now, TimeSpan.FromMinutes(1)));
        Assert.Throws<DomainException>(() => DeliveryOtp.Create(Guid.NewGuid(), Guid.NewGuid(), "h", TestData.Now, TimeSpan.Zero));
    }
}

public sealed class ProofImageTests
{
    [Fact]
    public void PngJpegAndWebp_AreRecognisedByTheirFirstBytes()
    {
        Assert.Equal("png", ProofImage.DetectExtension([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]));
        Assert.Equal("jpg", ProofImage.DetectExtension([0xFF, 0xD8, 0xFF, 0xE1, 0, 0]));
        Assert.Equal("webp", ProofImage.DetectExtension("RIFF\0\0\0\0WEBPVP8 "u8));
    }

    [Theory]
    [InlineData("GIF89a")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("%PDF-1.7")]
    [InlineData("")]
    public void AnythingElse_IsRefused(string text)
    {
        Assert.Null(ProofImage.DetectExtension(System.Text.Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void ARiffFileThatIsNotWebp_IsRefused()
    {
        Assert.Null(ProofImage.DetectExtension("RIFF\0\0\0\0WAVEfmt "u8));
        Assert.Null(ProofImage.DetectExtension([0x89, 0x50, 0x4E, 0x47]));
    }

    [Theory]
    [InlineData("2026/11/abc.png", "image/png")]
    [InlineData("2026/11/abc.jpg", "image/jpeg")]
    [InlineData("2026/11/abc.webp", "image/webp")]
    [InlineData("2026/11/abc.exe", "application/octet-stream")]
    public void TheMediaType_ComesFromTheStoredKey(string key, string expected) =>
        Assert.Equal(expected, ProofImage.ContentTypeFor(key));
}

/// <summary>The shared status changer keeps a delivery assignment in step with its parcel, whoever changes the status.</summary>
public sealed class StatusChangerDeliverySyncTests
{
    private readonly NetworkSetup _net = new();

    private DeliveryAssignment Open(Parcel parcel, bool started = false)
    {
        var assignment = DeliveryAssignment.Assign(parcel.Id, Guid.NewGuid(), AssignmentType.Delivery, TestData.Now);
        if (started)
        {
            assignment.Start(TestData.Now.AddHours(1));
        }

        _net.Deliveries.GetOpenForParcelAsync(parcel.Id, AssignmentType.Delivery, Arg.Any<CancellationToken>()).Returns(assignment);
        return assignment;
    }

    private Task Apply(Parcel parcel, ParcelStatusCode target, string? remarks = null, bool atBranch = false) =>
        _net.Changer.ApplyAsync(parcel, target, remarks, atBranch ? _net.Kathmandu.Id : null, null, null, CancellationToken.None);

    [Fact]
    public async Task OutForDelivery_StartsAWaitingAssignment()
    {
        var parcel = ParcelTests.ParcelAt(ParcelStatusCode.ArrivedAtWarehouse);
        var assignment = Open(parcel);

        await Apply(parcel, ParcelStatusCode.OutForDelivery, atBranch: true);

        Assert.Equal(AssignmentStatus.InProgress, assignment.Status);
        Assert.Equal(_net.Clock.GetUtcNow(), assignment.StartedAtUtc);
    }

    [Fact]
    public async Task Delivered_CompletesTheAssignment()
    {
        var parcel = ParcelTests.ParcelAt(ParcelStatusCode.OutForDelivery);
        var assignment = Open(parcel, started: true);

        await Apply(parcel, ParcelStatusCode.Delivered);

        Assert.Equal(AssignmentStatus.Completed, assignment.Status);
        Assert.Equal(_net.Clock.GetUtcNow(), assignment.CompletedAtUtc);
    }

    [Fact]
    public async Task DeliveryFailed_FailsTheAssignment_WithTheRemarksAsTheReason()
    {
        var parcel = ParcelTests.ParcelAt(ParcelStatusCode.OutForDelivery);
        var assignment = Open(parcel, started: true);

        await Apply(parcel, ParcelStatusCode.DeliveryFailed, "Receiver not at home");

        Assert.Equal(AssignmentStatus.Failed, assignment.Status);
        Assert.Equal("Receiver not at home", assignment.FailureReason);
    }

    [Fact]
    public async Task ALongReason_IsShortenedToFitTheAssignment()
    {
        var parcel = ParcelTests.ParcelAt(ParcelStatusCode.OutForDelivery);
        var assignment = Open(parcel, started: true);

        await Apply(parcel, ParcelStatusCode.DeliveryFailed, new string('x', 450));

        Assert.Equal(300, assignment.FailureReason!.Length);
    }

    [Fact]
    public async Task Cancelling_TakesBackAnAssignmentThatHasNotStarted()
    {
        var parcel = TestData.NewParcel();
        var assignment = Open(parcel);

        await Apply(parcel, ParcelStatusCode.Cancelled, "Sender changed their mind");

        Assert.Equal(AssignmentStatus.Cancelled, assignment.Status);
    }

    [Theory]
    [InlineData(ParcelStatusCode.InTransit)]
    [InlineData(ParcelStatusCode.OutForDelivery)]
    public async Task MovingBetweenHubs_LeavesAWaitingAssignmentAlone(ParcelStatusCode target)
    {
        var parcel = ParcelTests.ParcelAt(ParcelStatusCode.ArrivedAtWarehouse);
        var assignment = Open(parcel);

        if (target == ParcelStatusCode.InTransit)
        {
            await Apply(parcel, target);
            Assert.Equal(AssignmentStatus.Assigned, assignment.Status);
        }
        else
        {
            await Apply(parcel, target, atBranch: true);
            Assert.Equal(AssignmentStatus.InProgress, assignment.Status);
        }
    }

    [Fact]
    public async Task ArrivingAtAWarehouse_DoesNotTouchAWaitingAssignment()
    {
        var parcel = ParcelTests.ParcelAt(ParcelStatusCode.InTransit);
        var assignment = Open(parcel);
        _net.AssignMe(_net.Kathmandu);

        await _net.Changer.ApplyAsync(parcel, ParcelStatusCode.ArrivedAtWarehouse, null, null, _net.Hub.Id, null, CancellationToken.None);

        Assert.Equal(AssignmentStatus.Assigned, assignment.Status);
    }

    [Fact]
    public async Task WithNoOpenAssignment_NothingHappens()
    {
        var parcel = ParcelTests.ParcelAt(ParcelStatusCode.OutForDelivery);

        await Apply(parcel, ParcelStatusCode.Delivered);

        Assert.Equal(ParcelStatusCode.Delivered, parcel.Status);
    }
}

/// <summary>Everything an agent does starts by working out who they are. A wrong answer here would expose parcels.</summary>
public sealed class AgentContextTests
{
    private readonly IDeliveryAgentRepository _agents = Substitute.For<IDeliveryAgentRepository>();
    private readonly NepalParcel.Application.Common.Interfaces.ICurrentUser _user = Substitute.For<NepalParcel.Application.Common.Interfaces.ICurrentUser>();
    private readonly AgentContext _sut;
    private readonly Guid _userId = Guid.NewGuid();

    public AgentContextTests()
    {
        _sut = new AgentContext(_agents, _user);
        _user.UserId.Returns(_userId);
        _user.IsInRole(Roles.DeliveryAgent).Returns(true);
    }

    [Fact]
    public async Task AnActiveAgent_IsResolved()
    {
        var agent = DeliveryAgent.Create(_userId, Guid.NewGuid(), "Ram", "9841000111");
        _agents.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(agent);

        Assert.Same(agent, await _sut.RequireAgentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        _user.UserId.Returns((Guid?)null);

        await Assert.ThrowsAsync<UnauthorizedException>(() => _sut.RequireAgentAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(Roles.SuperAdmin)]
    [InlineData(Roles.BranchManager)]
    [InlineData(Roles.Customer)]
    public async Task AnyOtherRole_IsForbidden_EvenWithAnAgentProfile(string role)
    {
        _user.IsInRole(Arg.Any<string>()).Returns(false);
        _user.IsInRole(role).Returns(true);
        _agents.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(DeliveryAgent.Create(_userId, Guid.NewGuid(), "Ram", "9841000111"));

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.RequireAgentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ALoginWithoutAProfile_IsForbidden()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.RequireAgentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ASwitchedOffAgent_IsForbidden_EvenWithAStillValidToken()
    {
        var agent = DeliveryAgent.Create(_userId, Guid.NewGuid(), "Ram", "9841000111");
        agent.Deactivate();
        _agents.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(agent);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => _sut.RequireAgentAsync(CancellationToken.None));

        Assert.Contains("switched off", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class DeliveryValidatorTests
{
    private static IReadOnlyCollection<string> Failed(FluentValidation.Results.ValidationResult result) =>
        result.Errors.Select(e => e.PropertyName).Distinct().ToList();

    [Theory]
    [InlineData("", false)]
    [InlineData("no", false)]
    [InlineData("Receiver not at home", true)]
    public void AFailedDelivery_NeedsARealReason(string reason, bool valid) =>
        Assert.Equal(valid, new MarkDeliveryFailedRequestValidator().Validate(new MarkDeliveryFailedRequest(reason)).IsValid);

    [Fact]
    public void FailureText_HasLimits()
    {
        var validator = new MarkDeliveryFailedRequestValidator();

        Assert.False(validator.Validate(new MarkDeliveryFailedRequest(new string('x', 201))).IsValid);
        Assert.False(validator.Validate(new MarkDeliveryFailedRequest("Receiver not at home", new string('x', 251))).IsValid);
        Assert.True(validator.Validate(new MarkDeliveryFailedRequest(new string('x', 200), new string('y', 250))).IsValid);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(2500.5, true)]
    public void CollectedCash_MustBePositive(double amount, bool valid) =>
        Assert.Equal(valid, new CollectCodRequestValidator().Validate(new CollectCodRequest((decimal)amount)).IsValid);

    [Fact]
    public void CollectedCash_HasAtMostTwoDecimals() =>
        Assert.False(new CollectCodRequestValidator().Validate(new CollectCodRequest(10.123m)).IsValid);

    [Theory]
    [InlineData("123456", true)]
    [InlineData("000000", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12a456", false)]
    [InlineData("", false)]
    public void ADeliveryCode_IsSixDigits(string code, bool valid) =>
        Assert.Equal(valid, new VerifyDeliveryCodeRequestValidator().Validate(new VerifyDeliveryCodeRequest(code)).IsValid);

    [Fact]
    public void CreatingAnAgent_NeedsEveryDetail()
    {
        var validator = new CreateDeliveryAgentRequestValidator();
        var good = new CreateDeliveryAgentRequest("Ram Rider", "ram@example.test", "9841000111", "Aa1-longenoughpassword", Guid.NewGuid());

        Assert.True(validator.Validate(good).IsValid);
        Assert.Contains("FullName", Failed(validator.Validate(good with { FullName = "" })));
        Assert.Contains("Email", Failed(validator.Validate(good with { Email = "not-an-email" })));
        Assert.Contains("PhoneNumber", Failed(validator.Validate(good with { PhoneNumber = "123" })));
        Assert.Contains("Password", Failed(validator.Validate(good with { Password = "short" })));
        Assert.Contains("BranchId", Failed(validator.Validate(good with { BranchId = Guid.Empty })));
        Assert.Contains("VehicleNumber", Failed(validator.Validate(good with { VehicleNumber = new string('x', 31) })));
    }

    [Fact]
    public void AssignmentQueries_CheckTheirFilters()
    {
        var validator = new DeliveryAssignmentQueryValidator();

        Assert.True(validator.Validate(new DeliveryAssignmentQuery { Status = "Open" }).IsValid);
        Assert.True(validator.Validate(new DeliveryAssignmentQuery { Status = "failed" }).IsValid);
        Assert.False(validator.Validate(new DeliveryAssignmentQuery { Status = "Maybe" }).IsValid);
        Assert.False(validator.Validate(new DeliveryAssignmentQuery { Status = "3" }).IsValid);
        Assert.False(validator.Validate(new DeliveryAssignmentQuery { From = new DateOnly(2026, 11, 12), To = new DateOnly(2026, 11, 10) }).IsValid);
        Assert.False(validator.Validate(new DeliveryAssignmentQuery { PageSize = 101 }).IsValid);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("open", true)]
    [InlineData("History", true)]
    [InlineData("everything", false)]
    public void TheAgentsListScope_IsOpenOrHistory(string? scope, bool valid) =>
        Assert.Equal(valid, new AgentAssignmentQueryValidator().Validate(new AgentAssignmentQuery { Scope = scope }).IsValid);

    [Fact]
    public void AReport_CannotRunBackwards_OrOverAYear()
    {
        var validator = new AgentPerformanceQueryValidator();
        var day = new DateOnly(2026, 11, 1);

        Assert.True(validator.Validate(new AgentPerformanceQuery { From = day, To = day }).IsValid);
        Assert.True(validator.Validate(new AgentPerformanceQuery { From = day.AddDays(-365), To = day }).IsValid);
        Assert.False(validator.Validate(new AgentPerformanceQuery { From = day, To = day.AddDays(-1) }).IsValid);
        Assert.False(validator.Validate(new AgentPerformanceQuery { From = day.AddDays(-400), To = day }).IsValid);
    }

    [Fact]
    public void CancellingAnAssignment_NeedsAReason() =>
        Assert.False(new CancelAssignmentRequestValidator().Validate(new CancelAssignmentRequest("no")).IsValid);
}

/// <summary>The report's arithmetic: rates, averages and totals.</summary>
public sealed class AgentPerformanceMathTests
{
    private readonly NetworkSetup _net = new();
    private readonly IDeliveryAgentRepository _agents = Substitute.For<IDeliveryAgentRepository>();
    private readonly ICodCollectionRepository _cods = Substitute.For<ICodCollectionRepository>();
    private readonly DeliveryAgentService _sut;

    public AgentPerformanceMathTests()
    {
        _net.SignInAs(Roles.SuperAdmin);
        _net.Branches.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, NepalParcel.Domain.Branches.Branch> { [_net.Kathmandu.Id] = _net.Kathmandu });
        _sut = new DeliveryAgentService(
            _agents,
            _net.Deliveries,
            _cods,
            _net.Branches,
            _net.Assignments,
            Substitute.For<IIdentityService>(),
            _net.Access,
            _net.Audit,
            _net.UnitOfWork,
            _net.CurrentUser,
            _net.Client,
            _net.Clock,
            new CreateDeliveryAgentRequestValidator(),
            new UpdateDeliveryAgentRequestValidator(),
            new DeliveryAgentQueryValidator(),
            new AgentPerformanceQueryValidator(),
            NullLogger<DeliveryAgentService>.Instance);
    }

    private static AssignmentStat Stat(Guid agent, AssignmentStatus status, int attempt = 1, double hours = 2) =>
        new(agent, status, attempt, TestData.Now, status == AssignmentStatus.Completed ? TestData.Now.AddHours(hours) : null);

    [Fact]
    public async Task RatesAveragesAndTotals_AreComputedFromTheAssignments()
    {
        var busy = DeliveryAgent.Create(Guid.NewGuid(), _net.Kathmandu.Id, "Busy Rider", "9841000111");
        var idle = DeliveryAgent.Create(Guid.NewGuid(), _net.Kathmandu.Id, "Idle Rider", "9841000222");
        _agents.GetForReportAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new[] { busy, idle });
        _net.Deliveries.GetStatsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Stat(busy.Id, AssignmentStatus.Completed, 1, 2),
                Stat(busy.Id, AssignmentStatus.Completed, 1, 4),
                Stat(busy.Id, AssignmentStatus.Completed, 2, 6), // a second attempt
                Stat(busy.Id, AssignmentStatus.Failed, 1),
                Stat(busy.Id, AssignmentStatus.Cancelled, 1),
                Stat(busy.Id, AssignmentStatus.InProgress, 1),
                Stat(busy.Id, AssignmentStatus.Assigned, 1),
            });
        _cods.GetStatsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new CodStat(busy.Id, 1500.50m), new CodStat(busy.Id, 499.50m) });

        var report = await _sut.GetPerformanceAsync(new AgentPerformanceQuery());

        var row = Assert.Single(report.Agents, r => r.DeliveryAgentId == busy.Id);
        Assert.Equal(7, row.Assigned);
        Assert.Equal(3, row.Delivered);
        Assert.Equal(1, row.Failed);
        Assert.Equal(1, row.Cancelled);
        Assert.Equal(2, row.InProgress);
        Assert.Equal(75.0m, row.SuccessRatePercent); // 3 delivered, 1 failed
        Assert.Equal(66.7m, row.FirstAttemptSuccessPercent); // first attempts: 2 delivered, 1 failed
        Assert.Equal(4.0m, row.AverageDeliveryHours); // (2 + 4 + 6) / 3
        Assert.Equal(2, row.CodCollections);
        Assert.Equal(2000.00m, row.CodCollected);
        Assert.Equal("Kathmandu Main", row.BranchName);

        var quiet = Assert.Single(report.Agents, r => r.DeliveryAgentId == idle.Id);
        Assert.Equal(0, quiet.Assigned);
        Assert.Null(quiet.SuccessRatePercent);
        Assert.Null(quiet.FirstAttemptSuccessPercent);
        Assert.Null(quiet.AverageDeliveryHours);
        Assert.Equal(0m, quiet.CodCollected);

        Assert.Equal(7, report.Totals.Assigned);
        Assert.Equal(3, report.Totals.Delivered);
        Assert.Equal(2000.00m, report.Totals.CodCollected);
        Assert.Equal("All agents", report.Totals.FullName);
    }

    [Fact]
    public async Task TheDefaultPeriod_IsTheLastThirtyNepalDays_IncludingToday()
    {
        _agents.GetForReportAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<DeliveryAgent>());
        _net.Deliveries.GetStatsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AssignmentStat>());
        _cods.GetStatsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CodStat>());

        var report = await _sut.GetPerformanceAsync(new AgentPerformanceQuery());

        var today = DateOnly.FromDateTime(_net.Clock.GetUtcNow().ToOffset(NepalTime.Offset).DateTime);
        Assert.Equal(today, report.To);
        Assert.Equal(today.AddDays(-29), report.From);
        await _net.Deliveries.Received(1).GetStatsAsync(
            Arg.Is<DateTimeOffset>(f => f == new DateTimeOffset(today.AddDays(-29).ToDateTime(TimeOnly.MinValue), NepalTime.Offset).ToUniversalTime()),
            Arg.Is<DateTimeOffset>(b => b == new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue), NepalTime.Offset).ToUniversalTime()),
            Arg.Any<Guid?>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }
}
