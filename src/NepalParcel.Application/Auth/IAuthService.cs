using NepalParcel.Contracts.Auth;

namespace NepalParcel.Application.Auth;

/// <summary>Registration, sign-in, token refresh (with rotation), sign-out and current-user use cases.</summary>
public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>Exchanges a refresh token for a new access + refresh token pair. The old refresh token stops working.</summary>
    Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>Revokes the whole session (all refresh tokens issued from the same login). Unknown tokens are ignored.</summary>
    Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default);

    Task<CurrentUserResponse> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}

/// <summary>Administrative account management.</summary>
public interface IUserAdministrationService
{
    Task<CurrentUserResponse> CreateStaffUserAsync(CreateStaffUserRequest request, CancellationToken cancellationToken = default);
}
