using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NepalParcel.Contracts.Auth;
using NepalParcel.Domain.Identity;
using NepalParcel.IntegrationTests.Infrastructure;

namespace NepalParcel.IntegrationTests;

public sealed class AuthEndpointsTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private static readonly Uri Register = new("/api/v1/auth/register", UriKind.Relative);
    private static readonly Uri Login = new("/api/v1/auth/login", UriKind.Relative);
    private static readonly Uri Refresh = new("/api/v1/auth/refresh", UriKind.Relative);
    private static readonly Uri Logout = new("/api/v1/auth/logout", UriKind.Relative);
    private static readonly Uri Me = new("/api/v1/auth/me", UriKind.Relative);
    private static readonly Uri AdminUsers = new("/api/v1/admin/users", UriKind.Relative);

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Register_CreatesCustomer_ReturnsTokens_AndNeverEchoesThePassword()
    {
        var email = TestUsers.NewEmail("customer");
        var password = TestUsers.NewPassword();

        var response = await _client.PostAsJsonAsync(Register, new RegisterRequest("Sita Sharma", email.ToUpperInvariant(), "+977 9841234567", password));
        var body = await response.Content.ReadAsStringAsync();
        var auth = JsonSerializer.Deserialize<AuthResponse>(body, JsonSerializerOptions.Web);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(auth);
        Assert.Equal([Roles.Customer], auth.User.Roles);
        Assert.Equal(email, auth.User.Email);
        Assert.Equal("Bearer", auth.TokenType);
        Assert.True(auth.AccessTokenExpiresAtUtc > DateTimeOffset.UtcNow);
        Assert.True(auth.RefreshTokenExpiresAtUtc > auth.AccessTokenExpiresAtUtc);
        Assert.DoesNotContain(password, body, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        var request = new RegisterRequest("Sita", TestUsers.NewEmail(), "9841234567", TestUsers.NewPassword());
        (await _client.PostAsJsonAsync(Register, request)).EnsureSuccessStatusCode();

        var response = await _client.PostAsJsonAsync(Register, request with { Email = request.Email.ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Register_WeakPassword_Returns400_WithPasswordErrors()
    {
        var response = await _client.PostAsJsonAsync(Register, new RegisterRequest("Sita", TestUsers.NewEmail(), "9841234567", "alllowercaseletters"));
        var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(errors.TryGetProperty("Password", out _));
    }

    [Fact]
    public async Task Register_InvalidInput_Returns400_WithFieldErrors()
    {
        var response = await _client.PostAsJsonAsync(Register, new RegisterRequest("", "nope", "12", "x"));
        var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(errors.TryGetProperty("FullName", out _));
        Assert.True(errors.TryGetProperty("Email", out _));
        Assert.True(errors.TryGetProperty("PhoneNumber", out _));
        Assert.True(errors.TryGetProperty("Password", out _));
    }

    [Fact]
    public async Task Login_Succeeds_WithCorrectPassword_AndTokenHoldsNoPersonalContactData()
    {
        var (email, password) = await RegisterAsync();

        var response = await _client.PostAsJsonAsync(Login, new LoginRequest(email, password));
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(auth);

        var payload = DecodeJwtPayload(auth.AccessToken);
        Assert.Contains(auth.User.Id.ToString(), payload, StringComparison.Ordinal);
        Assert.DoesNotContain("9841234567", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(password, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WrongPasswordAndUnknownUser_GiveTheSameGenericError()
    {
        var (email, _) = await RegisterAsync();

        var wrongPassword = await _client.PostAsJsonAsync(Login, new LoginRequest(email, TestUsers.NewPassword()));
        var unknownUser = await _client.PostAsJsonAsync(Login, new LoginRequest(TestUsers.NewEmail(), TestUsers.NewPassword()));

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(await Detail(wrongPassword), await Detail(unknownUser));
        Assert.Equal("Bearer", wrongPassword.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task Login_LocksAccount_AfterRepeatedFailures_EvenWithCorrectPassword()
    {
        var (email, password) = await RegisterAsync();

        for (var i = 0; i < 5; i++)
        {
            await _client.PostAsJsonAsync(Login, new LoginRequest(email, TestUsers.NewPassword()));
        }

        var response = await _client.PostAsJsonAsync(Login, new LoginRequest(email, password));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Too many", await Detail(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Me_RequiresToken_AndReturnsOnlyTheCallersOwnProfile()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(Me)).StatusCode);

        var (email, password) = await RegisterAsync();
        var auth = await LoginAsync(email, password);
        using var request = new HttpRequestMessage(HttpMethod.Get, Me);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var response = await _client.SendAsync(request);
        var me = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(email, me!.Email);
        Assert.Equal(auth.User.Id, me.Id);
        Assert.Equal([Roles.Customer], me.Roles);
    }

    [Fact]
    public async Task Me_WithTamperedToken_Returns401()
    {
        var (email, password) = await RegisterAsync();
        var auth = await LoginAsync(email, password);
        using var request = new HttpRequestMessage(HttpMethod.Get, Me);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken[..^3] + "abc");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Refresh_RotatesTokens_AndTheOldRefreshTokenStopsWorking()
    {
        var (email, password) = await RegisterAsync();
        var first = await LoginAsync(email, password);

        var rotated = await _client.PostAsJsonAsync(Refresh, new RefreshTokenRequest(first.RefreshToken));
        var second = await rotated.Content.ReadFromJsonAsync<AuthResponse>();

        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.NotEqual(first.RefreshToken, second!.RefreshToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);

        var replay = await _client.PostAsJsonAsync(Refresh, new RefreshTokenRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReplayOfUsedToken_RevokesTheWholeSession()
    {
        var (email, password) = await RegisterAsync();
        var first = await LoginAsync(email, password);
        var second = await (await _client.PostAsJsonAsync(Refresh, new RefreshTokenRequest(first.RefreshToken)))
            .Content.ReadFromJsonAsync<AuthResponse>();

        // An attacker replays the already-used token ...
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync(Refresh, new RefreshTokenRequest(first.RefreshToken))).StatusCode);

        // ... so even the legitimate newest token is now dead and the user must sign in again.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync(Refresh, new RefreshTokenRequest(second!.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Refresh_UnknownOrEmptyToken_IsRejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync(Refresh, new RefreshTokenRequest("not-a-real-token"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync(Refresh, new RefreshTokenRequest(""))).StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheSession_AndIsIdempotent()
    {
        var (email, password) = await RegisterAsync();
        var auth = await LoginAsync(email, password);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync(Logout, new LogoutRequest(auth.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync(Logout, new LogoutRequest(auth.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync(Logout, new LogoutRequest("unknown-token"))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync(Refresh, new RefreshTokenRequest(auth.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesRotatedDescendants_OfTheSameSession()
    {
        var (email, password) = await RegisterAsync();
        var first = await LoginAsync(email, password);
        var second = await (await _client.PostAsJsonAsync(Refresh, new RefreshTokenRequest(first.RefreshToken)))
            .Content.ReadFromJsonAsync<AuthResponse>();

        await _client.PostAsJsonAsync(Logout, new LogoutRequest(second!.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync(Refresh, new RefreshTokenRequest(second.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task AdminCreateStaff_RequiresSuperAdmin()
    {
        var request = new CreateStaffUserRequest("Counter Staff", TestUsers.NewEmail("staff"), "9851000000", TestUsers.NewPassword(), Roles.BookingStaff);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync(AdminUsers, request)).StatusCode);

        var customer = await TestUsers.CreateClientAsync(factory, Roles.Customer);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync(AdminUsers, request)).StatusCode);

        var manager = await TestUsers.CreateClientAsync(factory, Roles.BranchManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(AdminUsers, request)).StatusCode);
    }

    [Theory]
    [InlineData(Roles.BranchManager)]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.DeliveryAgent)]
    [InlineData(Roles.Accountant)]
    public async Task AdminCreateStaff_AsSuperAdmin_CreatesUserWithRole_WhoCanSignIn(string role)
    {
        var admin = await TestUsers.CreateClientAsync(factory, Roles.SuperAdmin);
        var password = TestUsers.NewPassword();
        var request = new CreateStaffUserRequest("New Staff", TestUsers.NewEmail("staff"), "9851000000", password, role);

        var response = await admin.PostAsJsonAsync(AdminUsers, request);
        var created = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal([role], created!.Roles);

        var auth = await LoginAsync(request.Email, password);
        Assert.Equal([role], auth.User.Roles);
        Assert.Contains($"\"{role}\"", DecodeJwtPayload(auth.AccessToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Roles.SuperAdmin)]
    [InlineData(Roles.Customer)]
    [InlineData("Hacker")]
    public async Task AdminCreateStaff_RejectsNonStaffRoles(string role)
    {
        var admin = await TestUsers.CreateClientAsync(factory, Roles.SuperAdmin);

        var response = await admin.PostAsJsonAsync(AdminUsers,
            new CreateStaffUserRequest("X", TestUsers.NewEmail(), "9851000000", TestUsers.NewPassword(), role));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_DocumentsBearerSecurity_AndAuthEndpoints()
    {
        var json = await _client.GetStringAsync(new Uri("/swagger/v1/swagger.json", UriKind.Relative));

        Assert.Contains("/api/v1/auth/login", json, StringComparison.Ordinal);
        Assert.Contains("/api/v1/auth/refresh", json, StringComparison.Ordinal);
        Assert.Contains("\"bearer\"", json, StringComparison.Ordinal);
    }

    private async Task<(string Email, string Password)> RegisterAsync()
    {
        var email = TestUsers.NewEmail("customer");
        var password = TestUsers.NewPassword();
        var response = await _client.PostAsJsonAsync(Register, new RegisterRequest("Sita Sharma", email, "9841234567", password));
        response.EnsureSuccessStatusCode();
        return (email, password);
    }

    private async Task<AuthResponse> LoginAsync(string email, string password)
    {
        var response = await _client.PostAsJsonAsync(Login, new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<string?> Detail(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString();

    private static string DecodeJwtPayload(string jwt)
    {
        var segment = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        segment = segment.PadRight(segment.Length + ((4 - (segment.Length % 4)) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(segment));
    }
}
