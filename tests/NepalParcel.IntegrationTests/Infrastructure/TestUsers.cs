using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Auth;
using NepalParcel.Infrastructure.Identity;

namespace NepalParcel.IntegrationTests.Infrastructure;

/// <summary>Creates throwaway users with random credentials directly in the test database.</summary>
public static class TestUsers
{
    /// <summary>A random password that satisfies the policy. Never reused and never stored.</summary>
    public static string NewPassword() => "Aa1-" + Guid.NewGuid().ToString("N");

    public static string NewEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@example.test";

    public static async Task<(string Email, string Password)> CreateAsync(NepalParcelApiFactory factory, string role)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = NewEmail(role.ToLowerInvariant());
        var password = NewPassword();
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            FullName = $"Test {role}",
            PhoneNumber = "9841000000",
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return (email, password);
    }

    /// <summary>Creates a user in the role, signs in over HTTP and returns a client with the bearer token set.</summary>
    public static async Task<HttpClient> CreateClientAsync(NepalParcelApiFactory factory, string role) =>
        (await CreateClientWithIdAsync(factory, role)).Client;

    /// <summary>Like <see cref="CreateClientAsync"/>, and also returns the new user's id (for assigning them to a branch).</summary>
    public static async Task<(HttpClient Client, Guid UserId)> CreateClientWithIdAsync(NepalParcelApiFactory factory, string role)
    {
        var (email, password) = await CreateAsync(factory, role);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(new Uri("/api/v1/auth/login", UriKind.Relative), new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var userId = (await users.FindByEmailAsync(email))!.Id;
        return (client, userId);
    }
}
