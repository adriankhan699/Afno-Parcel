using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Contracts.Staff;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;
using static NepalParcel.IntegrationTests.Infrastructure.DeliveryTestHelpers;

namespace NepalParcel.IntegrationTests;

/// <summary>Creating and managing delivery agents, giving parcels to them, who may see what, and the performance report.</summary>
public sealed class DeliveryAgentManagementTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private readonly BookingTestHelpers _booking = new(factory);
    private readonly DeliveryTestHelpers _delivery = new(factory);

    // ---- creating and managing agents ----------------------------------------------------------------------------

    [Fact]
    public async Task AnAdmin_CreatesAnAgent_WhoCanSignInAndIsAssignedToTheBranch()
    {
        var branch = await _booking.CreateBranchAsync();

        var (client, agent, email, _) = await _delivery.NewAgentAsync(branch);

        Assert.Equal("Agent Ram", agent.FullName);
        Assert.Equal(email.ToLowerInvariant(), agent.Email);
        Assert.Equal(branch, agent.BranchId);
        Assert.Equal("Test Branch", agent.BranchName);
        Assert.Equal("BA 1 PA 1234", agent.VehicleNumber);
        Assert.True(agent.IsActive);
        Assert.True(agent.IsAvailable);
        Assert.Equal(0, agent.OpenAssignments);

        var me = await ReadAsync<AgentSummaryResponse>(await client.GetAsync(AgentMeUri));
        Assert.Equal(agent.Id, me.AgentId);
        var workplace = await ReadAsync<MyAssignmentResponse>(await client.GetAsync(new Uri("/api/v1/staff/me/assignment", UriKind.Relative)));
        Assert.Equal(branch, workplace.Assignment!.BranchId);
    }

    [Fact]
    public async Task AgentsAreListedAndFound_ByNamePhoneOrVehicle()
    {
        var branch = await _booking.CreateBranchAsync();
        var (_, agent, _, _) = await _delivery.NewAgentAsync(branch);
        var admin = await _delivery.AdminAsync();

        var all = await ReadAsync<PagedResponse<DeliveryAgentResponse>>(await admin.GetAsync(new Uri($"{AgentsUri}?branchId={branch}", UriKind.Relative)));
        var byPlate = await ReadAsync<PagedResponse<DeliveryAgentResponse>>(await admin.GetAsync(new Uri($"{AgentsUri}?search=PA%201234&branchId={branch}", UriKind.Relative)));
        var none = await ReadAsync<PagedResponse<DeliveryAgentResponse>>(await admin.GetAsync(new Uri($"{AgentsUri}?search=zzzzzz&branchId={branch}", UriKind.Relative)));
        var one = await ReadAsync<DeliveryAgentResponse>(await admin.GetAsync(AgentUri(agent.Id)));

        Assert.Equal(agent.Id, Assert.Single(all.Items).Id);
        Assert.Equal(agent.Id, Assert.Single(byPlate.Items).Id);
        Assert.Empty(none.Items);
        Assert.Equal(agent.Email, one.Email);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(AgentUri(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task CreatingAnAgent_ValidatesTheRequest_AndRefusesADuplicateLogin()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (_, _, email, _) = await _delivery.NewAgentAsync(branch);

        var badPhone = await admin.PostAsJsonAsync(AgentsUri, new CreateDeliveryAgentRequest("A B", TestUsers.NewEmail(), "123", TestUsers.NewPassword(), branch));
        var weakPassword = await admin.PostAsJsonAsync(AgentsUri, new CreateDeliveryAgentRequest("A B", TestUsers.NewEmail(), NewPhone(), "short", branch));
        var noBranch = await admin.PostAsJsonAsync(AgentsUri, new CreateDeliveryAgentRequest("A B", TestUsers.NewEmail(), NewPhone(), TestUsers.NewPassword(), Guid.Empty));
        var unknownBranch = await admin.PostAsJsonAsync(AgentsUri, new CreateDeliveryAgentRequest("A B", TestUsers.NewEmail(), NewPhone(), TestUsers.NewPassword(), Guid.NewGuid()));
        var duplicate = await admin.PostAsJsonAsync(AgentsUri, new CreateDeliveryAgentRequest("A B", email, NewPhone(), TestUsers.NewPassword(), branch));

        Assert.Equal(HttpStatusCode.BadRequest, badPhone.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, weakPassword.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noBranch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownBranch.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task ABranchManager_ManagesOnlyTheAgentsOfTheirOwnBranch()
    {
        var own = await _booking.CreateBranchAsync();
        var other = await _booking.CreateBranchAsync();
        var manager = await _booking.StaffAtAsync(Roles.BranchManager, own);
        var (_, foreign, _, _) = await _delivery.NewAgentAsync(other);

        var (_, mine, _, _) = await _delivery.NewAgentAsync(own, manager);
        var abroad = await manager.PostAsJsonAsync(AgentsUri, new CreateDeliveryAgentRequest("Far Away", TestUsers.NewEmail(), NewPhone(), TestUsers.NewPassword(), other));
        var list = await ReadAsync<PagedResponse<DeliveryAgentResponse>>(await manager.GetAsync(new Uri($"{AgentsUri}?pageSize=100", UriKind.Relative)));

        Assert.Equal(own, mine.BranchId);
        Assert.Equal(HttpStatusCode.Forbidden, abroad.StatusCode);
        Assert.Contains(list.Items, a => a.Id == mine.Id);
        Assert.DoesNotContain(list.Items, a => a.Id == foreign.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(AgentUri(foreign.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(new Uri($"{AgentsUri}?branchId={other}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync(AgentUri(foreign.Id), new UpdateDeliveryAgentRequest(NewPhone()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync(AgentUri(foreign.Id, "/deactivate"), null)).StatusCode);
    }

    [Fact]
    public async Task AManagerWhoIsNotAssignedToABranch_CannotManageAgents()
    {
        var branch = await _booking.CreateBranchAsync();
        var unassigned = await _booking.StaffAsync(Roles.BranchManager);

        Assert.Equal(HttpStatusCode.Forbidden, (await unassigned.GetAsync(AgentsUri)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await unassigned.PostAsJsonAsync(AgentsUri, new CreateDeliveryAgentRequest("A B", TestUsers.NewEmail(), NewPhone(), TestUsers.NewPassword(), branch))).StatusCode);
    }

    [Theory]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.DeliveryAgent)]
    [InlineData(Roles.Customer)]
    public async Task OnlyManagers_CanUseTheAgentAndAssignmentEndpoints(string role)
    {
        var client = await _booking.StaffAsync(role);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(AgentsUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(AgentUri(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri($"{AgentsUri}/performance", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(ReadyUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(AssignmentsUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(AssignmentsUri, new AssignDeliveryRequest(id, id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(ManagerAssignmentUri(id, "/cancel"), new CancelAssignmentRequest("not needed"))).StatusCode);
    }

    [Fact]
    public async Task WithoutSigningIn_EverythingReturns401()
    {
        var anonymous = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(AgentsUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(AssignmentsUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(AgentMeUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(AgentAssignmentUri(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(AgentAssignmentUri(id, "/start"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri($"/api/v1/delivery/proofs/{id}/file", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task AnAgentsDetails_CanBeEdited_ButNotTheirNameOrBranch()
    {
        var branch = await _booking.CreateBranchAsync();
        var (_, agent, _, _) = await _delivery.NewAgentAsync(branch);
        var admin = await _delivery.AdminAsync();

        var response = await admin.PutAsJsonAsync(AgentUri(agent.Id), new UpdateDeliveryAgentRequest("9801112233", "Van", "ba 2 cha 99", "LIC-9"));
        var updated = await ReadAsync<DeliveryAgentResponse>(response);

        Assert.Equal("9801112233", updated.PhoneNumber);
        Assert.Equal("Van", updated.VehicleType);
        Assert.Equal("BA 2 CHA 99", updated.VehicleNumber);
        Assert.Equal(agent.FullName, updated.FullName);
        Assert.Equal(branch, updated.BranchId);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(AgentUri(agent.Id), new UpdateDeliveryAgentRequest("abc"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync(AgentUri(Guid.NewGuid()), new UpdateDeliveryAgentRequest(NewPhone()))).StatusCode);
    }

    [Fact]
    public async Task ASwitchedOffAgent_CannotSignIn_OrUseAnExistingSession_UntilSwitchedBackOn()
    {
        var branch = await _booking.CreateBranchAsync();
        var (client, agent, email, password) = await _delivery.NewAgentAsync(branch);
        var admin = await _delivery.AdminAsync();

        var off = await admin.PostAsync(AgentUri(agent.Id, "/deactivate"), null);

        var switchedOff = await ReadAsync<DeliveryAgentResponse>(off);
        Assert.False(switchedOff.IsActive);
        Assert.False(switchedOff.IsAvailable);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(AgentMeUri)).StatusCode); // the token is still valid, the profile is not
        var login = await factory.CreateClient().PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new NepalParcel.Contracts.Auth.LoginRequest(email, password));
        Assert.False(login.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(AgentUri(agent.Id, "/deactivate"), null)).StatusCode);

        var on = await admin.PostAsync(AgentUri(agent.Id, "/activate"), null);
        Assert.True((await ReadAsync<DeliveryAgentResponse>(on)).IsActive);
        var again = await _delivery.SignInAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync(AgentMeUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(AgentUri(agent.Id, "/activate"), null)).StatusCode);
    }

    [Fact]
    public async Task AnAgentWithOpenAssignments_CannotBeSwitchedOff()
    {
        var branch = await _booking.CreateBranchAsync();
        var (_, agent, _, _) = await _delivery.NewAgentAsync(branch);
        var admin = await _delivery.AdminAsync();
        var parcel = await _delivery.ReadyParcelAsync(branch);
        var assignment = await _delivery.AssignAsync(admin, parcel.Id, agent.Id);

        var refused = await admin.PostAsync(AgentUri(agent.Id, "/deactivate"), null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(1, (await ReadAsync<DeliveryAgentResponse>(await admin.GetAsync(AgentUri(agent.Id)))).OpenAssignments);
        (await admin.PostAsJsonAsync(ManagerAssignmentUri(assignment.Id, "/cancel"), new CancelAssignmentRequest("Agent is off sick"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(AgentUri(agent.Id, "/deactivate"), null)).StatusCode);
    }

    [Fact]
    public async Task AnAgent_CanMarkThemselvesUnavailable_AndThenCannotBeGivenParcels()
    {
        var branch = await _booking.CreateBranchAsync();
        var (client, agent, _, _) = await _delivery.NewAgentAsync(branch);
        var admin = await _delivery.AdminAsync();
        var parcel = await _delivery.ReadyParcelAsync(branch);

        var summary = await ReadAsync<AgentSummaryResponse>(await client.PutAsJsonAsync(new Uri("/api/v1/agent/me/availability", UriKind.Relative), new SetAvailabilityRequest(false)));
        var refused = await admin.PostAsJsonAsync(AssignmentsUri, new AssignDeliveryRequest(parcel.Id, agent.Id));

        Assert.False(summary.IsAvailable);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("not available", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var back = await admin.PutAsJsonAsync(AgentUri(agent.Id, "/availability"), new SetAvailabilityRequest(true));
        Assert.True((await ReadAsync<DeliveryAgentResponse>(back)).IsAvailable);
        await _delivery.AssignAsync(admin, parcel.Id, agent.Id);
    }

    // ---- giving parcels to agents --------------------------------------------------------------------------------

    [Fact]
    public async Task AManager_SeesTheParcelsReadyForDelivery_AndAssigningOneRemovesItFromTheList()
    {
        var branch = await _booking.CreateBranchAsync();
        var manager = await _booking.StaffAtAsync(Roles.BranchManager, branch);
        var (client, agent, _, _) = await _delivery.NewAgentAsync(branch);
        var parcel = await _delivery.ReadyParcelAsync(branch);
        var notReady = await BookAsync(await _booking.CustomerAsync(), NewBooking(branch));

        var before = await ReadAsync<PagedResponse<ReadyParcelResponse>>(await manager.GetAsync(ReadyUri));
        var assignment = await _delivery.AssignAsync(manager, parcel.Id, agent.Id);
        var after = await ReadAsync<PagedResponse<ReadyParcelResponse>>(await manager.GetAsync(ReadyUri));

        var row = Assert.Single(before.Items, p => p.ParcelId == parcel.Id);
        Assert.Equal("ArrivedAtWarehouse", row.Status);
        Assert.Equal(0, row.FailedAttempts);
        Assert.DoesNotContain(before.Items, p => p.ParcelId == notReady.Id);
        Assert.DoesNotContain(after.Items, p => p.ParcelId == parcel.Id);

        Assert.Equal("Assigned", assignment.Status);
        Assert.Equal(1, assignment.AttemptNumber);
        Assert.Equal(agent.Id, assignment.DeliveryAgentId);
        Assert.Equal(parcel.TrackingNumber, assignment.TrackingNumber);
        Assert.Equal(1, (await ReadAsync<DeliveryAgentResponse>(await manager.GetAsync(AgentUri(agent.Id)))).OpenAssignments);

        var mine = await ReadAsync<PagedResponse<AgentAssignmentResponse>>(await client.GetAsync(new Uri("/api/v1/agent/assignments", UriKind.Relative)));
        var seen = Assert.Single(mine.Items);
        Assert.Equal(parcel.TrackingNumber, seen.TrackingNumber);
        Assert.False(string.IsNullOrWhiteSpace(seen.ReceiverPhone));
        Assert.Contains("Pokhara", seen.ReceiverAddress, StringComparison.Ordinal);
        Assert.True(seen.CanStart);
        Assert.False(seen.CanComplete);
    }

    [Fact]
    public async Task AParcelThatIsNotReady_OrAlreadyAssigned_CannotBeAssigned()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (_, first, _, _) = await _delivery.NewAgentAsync(branch);
        var (_, second, _, _) = await _delivery.NewAgentAsync(branch);
        var parcel = await _delivery.ReadyParcelAsync(branch);
        var fresh = await BookAsync(await _booking.CustomerAsync(), NewBooking(branch));
        var delivered = await _delivery.ReadyParcelAsync(branch);
        await _booking.AdvanceAsync(delivered.Id, ParcelStatusCode.OutForDelivery, ParcelStatusCode.Delivered);

        await _delivery.AssignAsync(admin, parcel.Id, first.Id);
        var twice = await admin.PostAsJsonAsync(AssignmentsUri, new AssignDeliveryRequest(parcel.Id, second.Id));
        var tooEarly = await admin.PostAsJsonAsync(AssignmentsUri, new AssignDeliveryRequest(fresh.Id, first.Id));
        var tooLate = await admin.PostAsJsonAsync(AssignmentsUri, new AssignDeliveryRequest(delivered.Id, first.Id));

        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooEarly.StatusCode);
        Assert.Contains("not ready for delivery", await tooEarly.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(AssignmentsUri, new AssignDeliveryRequest(Guid.NewGuid(), first.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(AssignmentsUri, new AssignDeliveryRequest(parcel.Id, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(AssignmentsUri, new AssignDeliveryRequest(Guid.Empty, first.Id))).StatusCode);
    }

    [Fact]
    public async Task TheDatabase_NeverAllowsTwoOpenDeliveryAssignmentsForOneParcel()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (_, first, _, _) = await _delivery.NewAgentAsync(branch);
        var (_, second, _, _) = await _delivery.NewAgentAsync(branch);
        var parcel = await _delivery.ReadyParcelAsync(branch);
        await _delivery.AssignAsync(admin, parcel.Id, first.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        db.DeliveryAssignments.Add(DeliveryAssignment.Assign(parcel.Id, second.Id, AssignmentType.Delivery, DateTimeOffset.UtcNow, null, 2));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ABranchManager_AssignsOnlyTheirOwnAgents_ToParcelsAtOrHeadingToTheirBranch()
    {
        var own = await _booking.CreateBranchAsync();
        var other = await _booking.CreateBranchAsync();
        var manager = await _booking.StaffAtAsync(Roles.BranchManager, own);
        var (_, mine, _, _) = await _delivery.NewAgentAsync(own);
        var (_, foreign, _, _) = await _delivery.NewAgentAsync(other);
        var ownParcel = await _delivery.ReadyParcelAsync(own);
        var foreignParcel = await _delivery.ReadyParcelAsync(other);

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(AssignmentsUri, new AssignDeliveryRequest(ownParcel.Id, foreign.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(AssignmentsUri, new AssignDeliveryRequest(foreignParcel.Id, mine.Id))).StatusCode);
        await _delivery.AssignAsync(manager, ownParcel.Id, mine.Id);

        var ready = await ReadAsync<PagedResponse<ReadyParcelResponse>>(await manager.GetAsync(new Uri($"{ReadyUri}?pageSize=100", UriKind.Relative)));
        Assert.DoesNotContain(ready.Items, p => p.ParcelId == foreignParcel.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(new Uri($"{ReadyUri}?branchId={other}", UriKind.Relative))).StatusCode);

        // A super administrator can put any agent on any parcel.
        var admin = await _delivery.AdminAsync();
        await _delivery.AssignAsync(admin, foreignParcel.Id, mine.Id);
    }

    [Fact]
    public async Task AnAssignmentCanBeTakenBack_BeforeTheAgentStarts_AndThenGivenToSomeoneElse()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (firstClient, first, _, _) = await _delivery.NewAgentAsync(branch);
        var (_, second, _, _) = await _delivery.NewAgentAsync(branch);
        var parcel = await _delivery.ReadyParcelAsync(branch);
        var assignment = await _delivery.AssignAsync(admin, parcel.Id, first.Id);

        var noReason = await admin.PostAsJsonAsync(ManagerAssignmentUri(assignment.Id, "/cancel"), new CancelAssignmentRequest("no"));
        var cancelled = await ReadAsync<DeliveryAssignmentResponse>(await admin.PostAsJsonAsync(ManagerAssignmentUri(assignment.Id, "/cancel"), new CancelAssignmentRequest("Agent has a puncture")));
        var again = await _delivery.AssignAsync(admin, parcel.Id, second.Id);

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(2, again.AttemptNumber);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync(ManagerAssignmentUri(assignment.Id, "/cancel"), new CancelAssignmentRequest("Cancelling twice"))).StatusCode);
        var firstsOpen = await ReadAsync<PagedResponse<AgentAssignmentResponse>>(await firstClient.GetAsync(new Uri("/api/v1/agent/assignments", UriKind.Relative)));
        Assert.Empty(firstsOpen.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(ManagerAssignmentUri(Guid.NewGuid(), "/cancel"), new CancelAssignmentRequest("Does not exist"))).StatusCode);
    }

    [Fact]
    public async Task AnAssignmentTheAgentHasStarted_CannotBeTakenBack()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (client, agent, _, _) = await _delivery.NewAgentAsync(branch);
        var parcel = await _delivery.ReadyParcelAsync(branch);
        var assignment = await _delivery.StartedAsync(admin, client, parcel.Id, agent.Id);

        var refused = await admin.PostAsJsonAsync(ManagerAssignmentUri(assignment.Id, "/cancel"), new CancelAssignmentRequest("Changed my mind"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    [Fact]
    public async Task TheAssignmentList_CanBeFilteredByAgentParcelStatusAndDate()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (_, agent, _, _) = await _delivery.NewAgentAsync(branch);
        var (_, other, _, _) = await _delivery.NewAgentAsync(branch);
        var first = await _delivery.ReadyParcelAsync(branch);
        var second = await _delivery.ReadyParcelAsync(branch);
        var a = await _delivery.AssignAsync(admin, first.Id, agent.Id);
        await _delivery.AssignAsync(admin, second.Id, other.Id);
        (await admin.PostAsJsonAsync(ManagerAssignmentUri(a.Id, "/cancel"), new CancelAssignmentRequest("Wrong agent"))).EnsureSuccessStatusCode();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.75)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.75).AddDays(1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        async Task<PagedResponse<DeliveryAssignmentResponse>> QueryAsync(string query) =>
            await ReadAsync<PagedResponse<DeliveryAssignmentResponse>>(await admin.GetAsync(new Uri($"{AssignmentsUri}?branchId={branch}&{query}", UriKind.Relative)));

        Assert.Equal(2, (await QueryAsync(string.Empty)).TotalCount);
        Assert.Equal(1, (await QueryAsync($"deliveryAgentId={agent.Id}")).TotalCount);
        Assert.Equal(1, (await QueryAsync($"parcelId={second.Id}")).TotalCount);
        Assert.Equal(1, (await QueryAsync("status=Cancelled")).TotalCount);
        Assert.Equal(1, (await QueryAsync("status=Open")).TotalCount);
        Assert.Equal(2, (await QueryAsync($"from={today}&to={today}")).TotalCount);
        Assert.Equal(0, (await QueryAsync($"from={tomorrow}")).TotalCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(new Uri($"{AssignmentsUri}?status=Maybe", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(new Uri($"{AssignmentsUri}?from={tomorrow}&to={today}", UriKind.Relative))).StatusCode);
    }

    // ---- who can see what ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAgent_CanNeverSeeOrChangeAnotherAgentsAssignment()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (owner, ownerAgent, _, _) = await _delivery.NewAgentAsync(branch);
        var (intruder, _, _, _) = await _delivery.NewAgentAsync(branch);
        var parcel = await _delivery.ReadyParcelAsync(branch);
        var assignment = await _delivery.AssignAsync(admin, parcel.Id, ownerAgent.Id);

        var theirs = await ReadAsync<PagedResponse<AgentAssignmentResponse>>(await intruder.GetAsync(new Uri("/api/v1/agent/assignments", UriKind.Relative)));

        Assert.Empty(theirs.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync(AgentAssignmentUri(assignment.Id))).StatusCode);
        foreach (var action in new[] { "accept", "start", "delivery-code", "complete" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await intruder.PostAsJsonAsync(AgentAssignmentUri(assignment.Id, "/" + action), new { })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await intruder.PostAsJsonAsync(AgentAssignmentUri(assignment.Id, "/fail"), new MarkDeliveryFailedRequest("Receiver not at home"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.PostAsJsonAsync(AgentAssignmentUri(assignment.Id, "/collect-cod"), new CollectCodRequest(100m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.PostAsJsonAsync(AgentAssignmentUri(assignment.Id, "/proof/otp"), new VerifyDeliveryCodeRequest("123456"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.PostAsync(AgentAssignmentUri(assignment.Id, "/proof/file"), FileForm("Photo", PngBytes()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.PostAsJsonAsync(AgentAssignmentUri(assignment.Id, "/return-to-warehouse"), new ReturnToWarehouseRequest(Guid.NewGuid()))).StatusCode);

        // The real owner is untouched.
        Assert.Equal("Assigned", (await AgentViewAsync(owner, assignment.Id)).Status);
        Assert.Equal("ArrivedAtWarehouse", (await _delivery.ParcelAsAdminAsync(parcel.Id)).Status);
    }

    [Theory]
    [InlineData(Roles.SuperAdmin)]
    [InlineData(Roles.BranchManager)]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Customer)]
    public async Task OnlyDeliveryAgents_CanUseTheAgentApi(string role)
    {
        var client = await _booking.StaffAsync(role);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(AgentMeUri)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri("/api/v1/agent/assignments", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(AgentAssignmentUri(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(AgentAssignmentUri(id, "/start"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(AgentAssignmentUri(id, "/collect-cod"), new CollectCodRequest(1m))).StatusCode);
    }

    [Fact]
    public async Task ADeliveryAgentLoginWithNoAgentProfile_GetsNothing()
    {
        // A login that has the role but no profile (for example created by hand) reaches no parcel at all.
        var client = await _booking.StaffAsync(Roles.DeliveryAgent);

        var response = await client.GetAsync(AgentMeUri);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("agent profile", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgent_CannotUseTheStatusApi_OrReadParcels_EvenForTheirOwnAssignment()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (client, agent, _, _) = await _delivery.NewAgentAsync(branch);
        var parcel = await _delivery.ReadyParcelAsync(branch);
        await _delivery.AssignAsync(admin, parcel.Id, agent.Id);

        var status = await client.PostAsJsonAsync(StatusUri(parcel.Id), To("OutForDelivery", warehouseId: null, branchId: branch));
        var read = await client.GetAsync(new Uri($"/api/v1/parcels/{parcel.Id}", UriKind.Relative));
        var scan = await client.PostAsJsonAsync(
            new Uri("/api/v1/scans", UriKind.Relative),
            new NepalParcel.Contracts.Tracking.ScanRequest(parcel.TrackingNumber, "OutForDelivery", Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, scan.StatusCode);
        Assert.Equal("ArrivedAtWarehouse", (await _delivery.ParcelAsAdminAsync(parcel.Id)).Status);
    }

    // ---- the performance report ----------------------------------------------------------------------------------

    [Fact]
    public async Task ThePerformanceReport_CountsDeliveriesFailuresCashAndTimes_PerAgent()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (client, agent, _, _) = await _delivery.NewAgentAsync(branch);
        var (_, idle, _, _) = await _delivery.NewAgentAsync(branch);

        // One parcel delivered at the first attempt, with cash.
        var cod = await _delivery.ReadyParcelAsync(branch, NewBooking(branch, payment: "CashOnDelivery", cod: 1000m));
        var codJob = await _delivery.StartedAsync(admin, client, cod.Id, agent.Id);
        await FinishAsync(client, codJob.Id, cod.Id, collectCash: cod.CodAmount);

        // One that failed, was given out again, and was delivered at the second attempt.
        var retried = await _delivery.ReadyParcelAsync(branch);
        var failedJob = await _delivery.StartedAsync(admin, client, retried.Id, agent.Id);
        (await client.PostAsJsonAsync(AgentAssignmentUri(failedJob.Id, "/fail"), new MarkDeliveryFailedRequest("Receiver not at home"))).EnsureSuccessStatusCode();
        var retryJob = await _delivery.StartedAsync(admin, client, retried.Id, agent.Id);
        await FinishAsync(client, retryJob.Id, retried.Id);

        // One still waiting.
        var waiting = await _delivery.ReadyParcelAsync(branch);
        await _delivery.AssignAsync(admin, waiting.Id, agent.Id);

        var report = await ReadAsync<AgentPerformanceReport>(await admin.GetAsync(new Uri($"{AgentsUri}/performance?branchId={branch}", UriKind.Relative)));

        var row = Assert.Single(report.Agents, r => r.DeliveryAgentId == agent.Id);
        Assert.Equal(4, row.Assigned);
        Assert.Equal(2, row.Delivered);
        Assert.Equal(1, row.Failed);
        Assert.Equal(0, row.Cancelled);
        Assert.Equal(1, row.InProgress);
        Assert.Equal(66.7m, row.SuccessRatePercent); // 2 of 3 finished
        Assert.Equal(50.0m, row.FirstAttemptSuccessPercent); // first attempts that finished: the cash parcel worked, the retried one did not
        Assert.NotNull(row.AverageDeliveryHours);
        Assert.Equal(1, row.CodCollections);
        Assert.Equal(cod.CodAmount, row.CodCollected);

        var quiet = Assert.Single(report.Agents, r => r.DeliveryAgentId == idle.Id);
        Assert.Equal(0, quiet.Assigned);
        Assert.Null(quiet.SuccessRatePercent);
        Assert.Null(quiet.AverageDeliveryHours);

        Assert.Equal(4, report.Totals.Assigned);
        Assert.Equal(2, report.Totals.Delivered);
        Assert.Equal(cod.CodAmount, report.Totals.CodCollected);
    }

    private async Task FinishAsync(HttpClient agentClient, Guid assignmentId, Guid parcelId, decimal? collectCash = null)
    {
        if (collectCash is { } amount)
        {
            (await agentClient.PostAsJsonAsync(AgentAssignmentUri(assignmentId, "/collect-cod"), new CollectCodRequest(amount))).EnsureSuccessStatusCode();
        }

        await _delivery.ProveByCodeAsync(agentClient, assignmentId, parcelId);
        (await agentClient.PostAsJsonAsync(AgentAssignmentUri(assignmentId, "/complete"), new CompleteDeliveryRequest())).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ThePerformanceReport_CoversTheChosenPeriod_AndRejectsABadOne()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (_, agent, _, _) = await _delivery.NewAgentAsync(branch);
        var parcel = await _delivery.ReadyParcelAsync(branch);
        await _delivery.AssignAsync(admin, parcel.Id, agent.Id);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.75));
        string Day(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var inRange = await ReadAsync<AgentPerformanceReport>(await admin.GetAsync(new Uri($"{AgentsUri}/performance?branchId={branch}&from={Day(today)}&to={Day(today)}", UriKind.Relative)));
        var future = await ReadAsync<AgentPerformanceReport>(await admin.GetAsync(new Uri($"{AgentsUri}/performance?branchId={branch}&from={Day(today.AddDays(1))}&to={Day(today.AddDays(2))}", UriKind.Relative)));
        var defaults = await ReadAsync<AgentPerformanceReport>(await admin.GetAsync(new Uri($"{AgentsUri}/performance?branchId={branch}", UriKind.Relative)));

        Assert.Equal(1, inRange.Totals.Assigned);
        Assert.Equal(0, future.Totals.Assigned);
        Assert.Equal(today, defaults.To);
        Assert.Equal(today.AddDays(-29), defaults.From);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(new Uri($"{AgentsUri}/performance?from={Day(today)}&to={Day(today.AddDays(-1))}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(new Uri($"{AgentsUri}/performance?from={Day(today.AddDays(-400))}&to={Day(today)}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task ABranchManager_SeesTheReportOfTheirOwnBranchOnly()
    {
        var own = await _booking.CreateBranchAsync();
        var other = await _booking.CreateBranchAsync();
        var manager = await _booking.StaffAtAsync(Roles.BranchManager, own);
        var (_, mine, _, _) = await _delivery.NewAgentAsync(own);
        var (_, foreign, _, _) = await _delivery.NewAgentAsync(other);

        var report = await ReadAsync<AgentPerformanceReport>(await manager.GetAsync(new Uri($"{AgentsUri}/performance", UriKind.Relative)));

        Assert.Contains(report.Agents, r => r.DeliveryAgentId == mine.Id);
        Assert.DoesNotContain(report.Agents, r => r.DeliveryAgentId == foreign.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(new Uri($"{AgentsUri}/performance?branchId={other}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _booking.StaffAsync(Roles.BranchManager)).GetAsync(new Uri($"{AgentsUri}/performance", UriKind.Relative))).StatusCode);
    }

    // ---- the audit trail -----------------------------------------------------------------------------------------

    [Fact]
    public async Task CreatingAssigningAndCancelling_AreWrittenToTheAuditTrail()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (_, agent, _, _) = await _delivery.NewAgentAsync(branch);
        var parcel = await _delivery.ReadyParcelAsync(branch);
        var assignment = await _delivery.AssignAsync(admin, parcel.Id, agent.Id);
        (await admin.PostAsJsonAsync(ManagerAssignmentUri(assignment.Id, "/cancel"), new CancelAssignmentRequest("Rider fell ill"))).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var forAgent = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == agent.Id.ToString()).Select(a => a.Action).ToListAsync();
        var forAssignment = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == assignment.Id.ToString()).ToListAsync();

        Assert.Contains("AgentCreated", forAgent);
        Assert.Contains(forAssignment, a => a.Action == "DeliveryAssigned" && a.UserId is not null);
        var cancelled = Assert.Single(forAssignment, a => a.Action == "DeliveryAssignmentCancelled");
        Assert.Contains("Rider fell ill", cancelled.NewValues, StringComparison.Ordinal);
    }
}
