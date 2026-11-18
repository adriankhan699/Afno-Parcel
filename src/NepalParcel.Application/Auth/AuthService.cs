using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Privacy;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Contracts.Auth;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Identity;

namespace NepalParcel.Application.Auth;

internal sealed partial class AuthService(
    IIdentityService identity,
    ITokenService tokens,
    IRefreshTokenRepository refreshTokens,
    ICustomerRepository customers,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<RegisterRequest> registerValidator,
    IValidator<LoginRequest> loginValidator,
    IValidator<RefreshTokenRequest> refreshValidator,
    IValidator<LogoutRequest> logoutValidator,
    ILogger<AuthService> logger) : IAuthService
{
    private const string InvalidCredentials = "Invalid email or password.";
    private const string InvalidRefreshToken = "The refresh token is invalid or has expired.";
    private const string RotatedReason = "Rotated";

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await registerValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var created = await identity
            .CreateUserAsync(request.FullName.Trim(), request.Email.Trim(), request.PhoneNumber, request.Password, Roles.Customer, cancellationToken)
            .ConfigureAwait(false);
        if (!created.Succeeded)
        {
            throw created.Failures.ToException();
        }

        var user = created.User!;
        try
        {
            customers.Add(Customer.Create(user.FullName, request.PhoneNumber, user.Email, user.Id));
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Identity and the customer profile are saved separately: do not leave an account without a profile.
            unitOfWork.DiscardPendingChanges();
            await identity.DeleteUserAsync(user.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var response = CreateSession(user, previous: null);
        AddAudit(AuditActions.Register, user.Id, user.Id, newValues: new { role = Roles.Customer });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogRegistered(logger, user.Id);
        return response;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await loginValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var check = await identity.CheckPasswordAsync(request.Email.Trim(), request.Password, cancellationToken).ConfigureAwait(false);
        if (check.Status != PasswordCheckStatus.Success || check.User is null)
        {
            AddAudit(AuditActions.LoginFailed, check.User?.Id, entityId: check.User?.Id, newValues: new { email = PrivacyMasking.MaskEmail(request.Email), reason = check.Status.ToString() });
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            LogLoginFailed(logger, check.Status);
            throw new UnauthorizedException(check.Status == PasswordCheckStatus.LockedOut
                ? "Too many failed sign-in attempts. Try again later."
                : InvalidCredentials);
        }

        var response = CreateSession(check.User, previous: null);
        AddAudit(AuditActions.Login, check.User.Id, check.User.Id);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogLoggedIn(logger, check.User.Id);
        return response;
    }

    public async Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await refreshValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var stored = await refreshTokens.FindByHashAsync(tokens.HashRefreshToken(request.RefreshToken), cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedException(InvalidRefreshToken);

        if (stored.IsRevoked)
        {
            // A rotated (already used) token is being replayed: assume it was stolen and end the whole session.
            if (string.Equals(stored.RevokedReason, RotatedReason, StringComparison.Ordinal))
            {
                await RevokeFamilyAsync(stored.FamilyId, "Reuse detected", now, cancellationToken).ConfigureAwait(false);
                AddAudit(AuditActions.TokenReuseDetected, stored.UserId, stored.UserId);
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                LogReuseDetected(logger, stored.UserId);
            }

            throw new UnauthorizedException(InvalidRefreshToken);
        }

        if (stored.IsExpired(now))
        {
            throw new UnauthorizedException(InvalidRefreshToken);
        }

        var user = await identity.FindByIdAsync(stored.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            await RevokeFamilyAsync(stored.FamilyId, "User unavailable", now, cancellationToken).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedException(InvalidRefreshToken);
        }

        var response = CreateSession(user, stored);
        AddAudit(AuditActions.TokenRefreshed, user.Id, user.Id);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            // Two requests tried to rotate the same token at once; only one may win.
            throw new UnauthorizedException(InvalidRefreshToken);
        }

        return response;
    }

    public async Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await logoutValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var stored = await refreshTokens.FindByHashAsync(tokens.HashRefreshToken(request.RefreshToken), cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return;
        }

        await RevokeFamilyAsync(stored.FamilyId, "Logout", clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        AddAudit(AuditActions.Logout, stored.UserId, stored.UserId);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CurrentUserResponse> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("Authentication is required.");
        var user = await identity.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("Authentication is required.");
        }

        return user.ToResponse();
    }

    private AuthResponse CreateSession(UserInfo user, RefreshToken? previous)
    {
        var now = clock.GetUtcNow();
        var rawRefreshToken = tokens.GenerateRefreshToken();
        var hash = tokens.HashRefreshToken(rawRefreshToken);

        var stored = previous is null
            ? RefreshToken.Issue(user.Id, hash, now, tokens.RefreshTokenLifetime, client.IpAddress)
            : previous.Rotate(hash, now, tokens.RefreshTokenLifetime, client.IpAddress);
        refreshTokens.Add(stored);

        var access = tokens.CreateAccessToken(user);
        return new AuthResponse(access.Token, access.ExpiresAtUtc, rawRefreshToken, stored.ExpiresAtUtc, user.ToResponse());
    }

    private async Task RevokeFamilyAsync(Guid familyId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = await refreshTokens.GetActiveByFamilyAsync(familyId, now, cancellationToken).ConfigureAwait(false);
        foreach (var token in active)
        {
            token.Revoke(now, reason);
        }
    }

    private void AddAudit(string action, Guid? userId, Guid? entityId, object? newValues = null) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "User",
            entityId?.ToString(),
            userId,
            clock.GetUtcNow(),
            newValues: newValues is null ? null : JsonSerializer.Serialize(newValues),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "User {UserId} registered")]
    private static partial void LogRegistered(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "User {UserId} signed in")]
    private static partial void LogLoggedIn(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Sign-in failed: {Reason}")]
    private static partial void LogLoginFailed(ILogger logger, PasswordCheckStatus reason);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Warning, Message = "Refresh token reuse detected for user {UserId}; session revoked")]
    private static partial void LogReuseDetected(ILogger logger, Guid userId);
}
