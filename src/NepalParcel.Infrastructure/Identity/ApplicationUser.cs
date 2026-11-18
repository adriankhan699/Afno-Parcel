using Microsoft.AspNetCore.Identity;

namespace NepalParcel.Infrastructure.Identity;

/// <summary>Login account. Passwords are stored only as salted PBKDF2 hashes by ASP.NET Core Identity.</summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public const int FullNameMaxLength = 150;

    public string FullName { get; set; } = string.Empty;

    /// <summary>Deactivated accounts cannot sign in or refresh tokens.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
    }

    public ApplicationRole(string name)
        : base(name)
    {
    }
}
