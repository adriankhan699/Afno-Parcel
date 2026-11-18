namespace NepalParcel.Contracts.Auth;

/// <summary>Self-service customer registration.</summary>
/// <param name="FullName">Customer's full name.</param>
/// <param name="Email">Login email. Must be unique.</param>
/// <param name="PhoneNumber">Nepali mobile or landline number.</param>
/// <param name="Password">At least 10 characters with upper-case, lower-case and a digit.</param>
public sealed record RegisterRequest(string FullName, string Email, string PhoneNumber, string Password);

/// <param name="Email">Account email.</param>
/// <param name="Password">Account password.</param>
public sealed record LoginRequest(string Email, string Password);

/// <param name="RefreshToken">The refresh token received from login, register or a previous refresh.</param>
public sealed record RefreshTokenRequest(string RefreshToken);

/// <param name="RefreshToken">The refresh token of the session to end.</param>
public sealed record LogoutRequest(string RefreshToken);

/// <summary>Creates a staff account (SuperAdmin only).</summary>
/// <param name="FullName">Staff member's full name.</param>
/// <param name="Email">Login email. Must be unique.</param>
/// <param name="PhoneNumber">Nepali mobile or landline number.</param>
/// <param name="Password">Initial password chosen by the administrator. Same rules as registration.</param>
/// <param name="Role">One of BranchManager, BookingStaff, WarehouseStaff, DeliveryAgent, Accountant.</param>
public sealed record CreateStaffUserRequest(string FullName, string Email, string? PhoneNumber, string Password, string Role);

/// <summary>The signed-in user. Contains only the user's own information.</summary>
/// <param name="Id">User id.</param>
/// <param name="Email">Login email.</param>
/// <param name="FullName">Full name.</param>
/// <param name="PhoneNumber">Phone number.</param>
/// <param name="Roles">Assigned roles.</param>
/// <param name="CreatedAtUtc">Account creation time in UTC. Display in Asia/Kathmandu.</param>
public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string FullName,
    string? PhoneNumber,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAtUtc);

/// <summary>Tokens issued after authentication. All timestamps are UTC.</summary>
/// <param name="AccessToken">Short-lived JWT; send as <c>Authorization: Bearer ...</c>.</param>
/// <param name="AccessTokenExpiresAtUtc">When the access token expires.</param>
/// <param name="RefreshToken">Single-use token to obtain a new pair; store securely.</param>
/// <param name="RefreshTokenExpiresAtUtc">When the refresh token expires.</param>
/// <param name="User">The authenticated user.</param>
public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc,
    CurrentUserResponse User)
{
    /// <summary>Always "Bearer".</summary>
    public string TokenType => "Bearer";
}
