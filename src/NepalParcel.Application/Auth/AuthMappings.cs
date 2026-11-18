using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Contracts.Auth;

namespace NepalParcel.Application.Auth;

internal static class AuthMappings
{
    public static CurrentUserResponse ToResponse(this UserInfo user) =>
        new(user.Id, user.Email, user.FullName, user.PhoneNumber, user.Roles, user.CreatedAtUtc);

    /// <summary>Turns Identity failures into the API's 409 / 400 error types.</summary>
    public static AppException ToException(this IReadOnlyList<IdentityFailure> failures)
    {
        if (failures.Any(f => f.Code is "DuplicateEmail" or "DuplicateUserName"))
        {
            return new ConflictException("An account with this email already exists.");
        }

        var errors = failures
            .GroupBy(f => f.Code.StartsWith("Password", StringComparison.Ordinal) ? "Password"
                : f.Code.Contains("Email", StringComparison.Ordinal) ? "Email"
                : "Account")
            .ToDictionary(g => g.Key, g => g.Select(f => f.Description).ToArray(), StringComparer.Ordinal);

        return new RequestValidationException(errors);
    }
}
