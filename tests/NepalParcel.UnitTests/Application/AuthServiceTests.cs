using Microsoft.Extensions.Logging.Abstractions;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Privacy;
using NepalParcel.Contracts.Auth;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Identity;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

public sealed class AuthServiceTests
{
    private const string OldToken = "old-refresh-token";
    private const string NewToken = "new-refresh-token";
    private static readonly string OldHash = new('a', 64);
    private static readonly string NewHash = new('b', 64);

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 17, 6, 0, 0, TimeSpan.Zero));
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();
    private readonly IRefreshTokenRepository _refreshTokens = Substitute.For<IRefreshTokenRepository>();
    private readonly ICustomerRepository _customers = Substitute.For<ICustomerRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IClientInfo _client = Substitute.For<IClientInfo>();
    private readonly UserInfo _user = new(Guid.NewGuid(), "sita@example.test", "Sita Sharma", "9841234567", [Roles.Customer], true, DateTimeOffset.UnixEpoch);
    private readonly AuthService _sut;

    public AuthServiceTests()
    {
        _tokens.RefreshTokenLifetime.Returns(TimeSpan.FromDays(7));
        _tokens.GenerateRefreshToken().Returns(NewToken);
        _tokens.HashRefreshToken(Arg.Any<string>()).Returns(call => call.Arg<string>() == OldToken ? OldHash : NewHash);
        _tokens.CreateAccessToken(Arg.Any<UserInfo>()).Returns(new AccessTokenResult("jwt", _clock.GetUtcNow().AddMinutes(15)));
        _client.IpAddress.Returns("10.0.0.5");

        _sut = new AuthService(
            _identity, _tokens, _refreshTokens, _customers, _audit, _unitOfWork, _currentUser, _client, _clock,
            new RegisterRequestValidator(), new LoginRequestValidator(), new RefreshTokenRequestValidator(), new LogoutRequestValidator(),
            NullLogger<AuthService>.Instance);
    }

    [Fact]
    public async Task Login_Success_IssuesTokens_AndStoresOnlyTheHash()
    {
        _identity.CheckPasswordAsync("sita@example.test", "irrelevant-password", Arg.Any<CancellationToken>())
            .Returns(new PasswordCheckResult(PasswordCheckStatus.Success, _user));

        var response = await _sut.LoginAsync(new LoginRequest("sita@example.test", "irrelevant-password"));

        Assert.Equal("jwt", response.AccessToken);
        Assert.Equal(NewToken, response.RefreshToken);
        Assert.Equal(_clock.GetUtcNow().AddDays(7), response.RefreshTokenExpiresAtUtc);
        _refreshTokens.Received(1).Add(Arg.Is<RefreshToken>(t => t.TokenHash == NewHash && t.UserId == _user.Id && t.CreatedByIp == "10.0.0.5"));
        _audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.Login));
    }

    [Theory]
    [InlineData(PasswordCheckStatus.InvalidCredentials, "Invalid email or password.")]
    [InlineData(PasswordCheckStatus.Inactive, "Invalid email or password.")]
    [InlineData(PasswordCheckStatus.LockedOut, "Too many failed sign-in attempts. Try again later.")]
    public async Task Login_Failure_ThrowsGenericUnauthorized_AuditsMasked_AndIssuesNothing(PasswordCheckStatus status, string expectedMessage)
    {
        _identity.CheckPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PasswordCheckResult(status, null));

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => _sut.LoginAsync(new LoginRequest("sita@example.test", "wrong-password")));

        Assert.Equal(expectedMessage, ex.Message);
        _refreshTokens.DidNotReceive().Add(Arg.Any<RefreshToken>());
        _audit.Received(1).Add(Arg.Is<AuditLog>(a =>
            a.Action == AuditActions.LoginFailed &&
            a.NewValues!.Contains("s***@example.test", StringComparison.Ordinal) &&
            !a.NewValues.Contains("sita@", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Refresh_ValidToken_RotatesAndRevokesTheOldOne()
    {
        var stored = RefreshToken.Issue(_user.Id, OldHash, _clock.GetUtcNow().AddDays(-1), TimeSpan.FromDays(7), null);
        _refreshTokens.FindByHashAsync(OldHash, Arg.Any<CancellationToken>()).Returns(stored);
        _identity.FindByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);

        var response = await _sut.RefreshAsync(new RefreshTokenRequest(OldToken));

        Assert.Equal(NewToken, response.RefreshToken);
        Assert.True(stored.IsRevoked);
        Assert.Equal("Rotated", stored.RevokedReason);
        _refreshTokens.Received(1).Add(Arg.Is<RefreshToken>(t => t.FamilyId == stored.FamilyId && t.TokenHash == NewHash));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_ReplayedRotatedToken_RevokesWholeFamily_AndAudits()
    {
        var old = RefreshToken.Issue(_user.Id, OldHash, _clock.GetUtcNow().AddDays(-1), TimeSpan.FromDays(7), null);
        var current = old.Rotate(NewHash, _clock.GetUtcNow().AddHours(-1), TimeSpan.FromDays(7), null);
        _refreshTokens.FindByHashAsync(OldHash, Arg.Any<CancellationToken>()).Returns(old);
        _refreshTokens.GetActiveByFamilyAsync(old.FamilyId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([current]);

        await Assert.ThrowsAsync<UnauthorizedException>(() => _sut.RefreshAsync(new RefreshTokenRequest(OldToken)));

        Assert.True(current.IsRevoked);
        Assert.Equal("Reuse detected", current.RevokedReason);
        _audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.TokenReuseDetected));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_ExpiredOrUnknownToken_IsRejected()
    {
        var expired = RefreshToken.Issue(_user.Id, OldHash, _clock.GetUtcNow().AddDays(-10), TimeSpan.FromDays(7), null);
        _refreshTokens.FindByHashAsync(OldHash, Arg.Any<CancellationToken>()).Returns(expired);

        await Assert.ThrowsAsync<UnauthorizedException>(() => _sut.RefreshAsync(new RefreshTokenRequest(OldToken)));
        await Assert.ThrowsAsync<UnauthorizedException>(() => _sut.RefreshAsync(new RefreshTokenRequest("never-issued")));
    }

    [Fact]
    public async Task Refresh_ConcurrentRotation_LosesWithUnauthorized()
    {
        var stored = RefreshToken.Issue(_user.Id, OldHash, _clock.GetUtcNow(), TimeSpan.FromDays(7), null);
        _refreshTokens.FindByHashAsync(OldHash, Arg.Any<CancellationToken>()).Returns(stored);
        _identity.FindByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns<Task<int>>(_ => throw new ConcurrencyException());

        await Assert.ThrowsAsync<UnauthorizedException>(() => _sut.RefreshAsync(new RefreshTokenRequest(OldToken)));
    }

    [Fact]
    public async Task Logout_RevokesWholeSession()
    {
        var stored = RefreshToken.Issue(_user.Id, OldHash, _clock.GetUtcNow(), TimeSpan.FromDays(7), null);
        _refreshTokens.FindByHashAsync(OldHash, Arg.Any<CancellationToken>()).Returns(stored);
        _refreshTokens.GetActiveByFamilyAsync(stored.FamilyId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([stored]);

        await _sut.LogoutAsync(new LogoutRequest(OldToken));

        Assert.True(stored.IsRevoked);
        Assert.Equal("Logout", stored.RevokedReason);
        _audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.Logout));
    }

    [Fact]
    public async Task Logout_UnknownToken_IsIgnored()
    {
        await _sut.LogoutAsync(new LogoutRequest("unknown"));

        _audit.DidNotReceive().Add(Arg.Any<AuditLog>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Register_WhenProfileSaveFails_RollsBackTheAccount()
    {
        _identity.CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Roles.Customer, Arg.Any<CancellationToken>())
            .Returns(new CreateUserResult(_user, []));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns<Task<int>>(_ => throw new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RegisterAsync(new RegisterRequest("Sita Sharma", "sita@example.test", "9841234567", "Str0ngPassword1")));

        _unitOfWork.Received(1).DiscardPendingChanges();
        await _identity.Received(1).DeleteUserAsync(_user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Register_DuplicateEmail_ThrowsConflict()
    {
        _identity.CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CreateUserResult(null, [new IdentityFailure("DuplicateEmail", "taken")]));

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.RegisterAsync(new RegisterRequest("Sita Sharma", "sita@example.test", "9841234567", "Str0ngPassword1")));
    }

    [Fact]
    public async Task Register_WeakPassword_MapsToPasswordValidationError()
    {
        _identity.CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CreateUserResult(null, [new IdentityFailure("PasswordRequiresDigit", "Passwords must have at least one digit.")]));

        var ex = await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.RegisterAsync(new RegisterRequest("Sita Sharma", "sita@example.test", "9841234567", "NoDigitsHereAtAll")));

        Assert.True(ex.Errors.ContainsKey("Password"));
    }

    [Fact]
    public void Validators_RejectShortPasswords_AndBadPhones()
    {
        var result = new RegisterRequestValidator().Validate(new RegisterRequest("Sita", "not-an-email", "123", "short"));

        var failed = result.Errors.Select(e => e.PropertyName).ToHashSet();
        Assert.Contains("Email", failed);
        Assert.Contains("PhoneNumber", failed);
        Assert.Contains("Password", failed);
    }

    [Fact]
    public void PrivacyMasking_HidesMostOfEmailAndPhone()
    {
        Assert.Equal("s***@example.test", PrivacyMasking.MaskEmail("sita@example.test"));
        Assert.Equal("98******67", PrivacyMasking.MaskPhone("9841234567"));
        Assert.Equal("***", PrivacyMasking.MaskEmail(null));
        Assert.Equal("***", PrivacyMasking.MaskPhone("12"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
