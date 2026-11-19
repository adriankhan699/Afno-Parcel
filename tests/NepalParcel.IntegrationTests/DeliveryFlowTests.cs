using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;
using static NepalParcel.IntegrationTests.Infrastructure.DeliveryTestHelpers;

namespace NepalParcel.IntegrationTests;

/// <summary>What an agent does: take a parcel out, prove the delivery, collect cash, complete it, or fail it and take it back.</summary>
public sealed class DeliveryFlowTests(NepalParcelApiFactory factory) : IClassFixture<NepalParcelApiFactory>
{
    private readonly BookingTestHelpers _booking = new(factory);
    private readonly DeliveryTestHelpers _delivery = new(factory);

    private sealed record Scene(Guid Branch, HttpClient Admin, HttpClient Agent, DeliveryAgentResponse AgentInfo, ParcelResponse Parcel);

    private async Task<Scene> NewSceneAsync(BookParcelRequest? booking = null, Guid? branch = null)
    {
        var branchId = branch ?? await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (client, info, _, _) = await _delivery.NewAgentAsync(branchId);
        var parcel = await _delivery.ReadyParcelAsync(branchId, booking);
        return new Scene(branchId, admin, client, info, parcel);
    }

    private static Uri Step(Guid assignmentId, string suffix) => AgentAssignmentUri(assignmentId, "/" + suffix);

    // ---- a successful delivery -----------------------------------------------------------------------------------

    [Fact]
    public async Task AnAgent_TakesAParcelOut_ProvesTheDeliveryWithACode_AndCompletesIt()
    {
        var s = await NewSceneAsync();
        var assignment = await _delivery.AssignAsync(s.Admin, s.Parcel.Id, s.AgentInfo.Id);

        var accepted = await ReadAsync<AgentAssignmentResponse>(await s.Agent.PostAsync(Step(assignment.Id, "accept"), null));
        var started = await ReadAsync<AgentAssignmentResponse>(await s.Agent.PostAsync(Step(assignment.Id, "start"), null));

        Assert.Equal("Accepted", accepted.Status);
        Assert.Equal("InProgress", started.Status);
        Assert.Equal("OutForDelivery", started.ParcelStatus);
        Assert.NotNull(started.StartedAtUtc);
        Assert.False(started.CanStart);
        Assert.False(started.CanComplete); // no proof yet
        Assert.True(started.CanFail);

        var early = await s.Agent.PostAsJsonAsync(Step(assignment.Id, "complete"), new CompleteDeliveryRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Contains("proof of delivery", await early.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var proved = await _delivery.ProveByCodeAsync(s.Agent, assignment.Id, s.Parcel.Id);
        Assert.True(proved.HasProof);
        Assert.True(proved.CanComplete);
        Assert.Equal("Otp", Assert.Single(proved.Proofs).Type);
        Assert.True(proved.Proofs[0].OtpVerified);
        Assert.False(proved.Proofs[0].HasFile);

        var done = await ReadAsync<AgentAssignmentResponse>(await s.Agent.PostAsJsonAsync(Step(assignment.Id, "complete"), new CompleteDeliveryRequest("Handed to the receiver")));

        Assert.Equal("Completed", done.Status);
        Assert.Equal("Delivered", done.ParcelStatus);
        Assert.NotNull(done.CompletedAtUtc);
        Assert.Null(done.ReceiverPhone); // no longer needed, so no longer sent
        Assert.Null(done.ReceiverAddress);
        Assert.False(done.CanComplete);

        var parcel = await _delivery.ParcelAsAdminAsync(s.Parcel.Id);
        Assert.Equal("Delivered", parcel.Status);
        Assert.NotNull(parcel.DeliveredAtUtc);
        Assert.Equal(["ArrivedAtWarehouse", "OutForDelivery", "Delivered"], parcel.History.TakeLast(3).Select(h => h.Status));
        Assert.Equal("Handed to the receiver", parcel.History[^1].Remarks);

        var publicView = await ReadAsync<PublicTrackingResponse>(await factory.CreateClient().GetAsync(new Uri($"/api/v1/tracking/{s.Parcel.TrackingNumber}", UriKind.Relative)));
        Assert.Equal("Delivered", publicView.Status);

        var history = await ReadAsync<PagedResponse<AgentAssignmentResponse>>(await s.Agent.GetAsync(new Uri("/api/v1/agent/assignments?scope=history", UriKind.Relative)));
        Assert.Equal(assignment.Id, Assert.Single(history.Items).Id);
        var open = await ReadAsync<PagedResponse<AgentAssignmentResponse>>(await s.Agent.GetAsync(new Uri("/api/v1/agent/assignments", UriKind.Relative)));
        Assert.Empty(open.Items);
        var summary = await ReadAsync<AgentSummaryResponse>(await s.Agent.GetAsync(AgentMeUri));
        Assert.Equal(1, summary.DeliveredToday);
        Assert.Equal(0, summary.OpenAssignments);
    }

    [Fact]
    public async Task StartingTheDelivery_RecordsTheHandOverAtTheWarehouse_ByTheAgent()
    {
        var s = await NewSceneAsync();
        var assignment = await _delivery.AssignAsync(s.Admin, s.Parcel.Id, s.AgentInfo.Id);

        (await s.Agent.PostAsync(Step(assignment.Id, "start"), null)).EnsureSuccessStatusCode();

        var movements = await ReadAsync<List<ParcelMovementResponse>>(await s.Admin.GetAsync(new Uri($"/api/v1/parcels/{s.Parcel.Id}/movements", UriKind.Relative)));
        var handOver = Assert.Single(movements);
        Assert.Equal("OutForDelivery", handOver.ScanType);
        Assert.Equal("Test Hub", handOver.WarehouseName);
        Assert.False(string.IsNullOrWhiteSpace(handOver.StaffName));
    }

    [Fact]
    public async Task ADeliveryCannotBeStartedTwice_OrCompletedWithoutBeingStarted()
    {
        var s = await NewSceneAsync();
        var assignment = await _delivery.AssignAsync(s.Admin, s.Parcel.Id, s.AgentInfo.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsJsonAsync(Step(assignment.Id, "complete"), new CompleteDeliveryRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsJsonAsync(Step(assignment.Id, "fail"), new MarkDeliveryFailedRequest("Receiver not at home"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsync(Step(assignment.Id, "delivery-code"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsJsonAsync(Step(assignment.Id, "collect-cod"), new CollectCodRequest(10m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsync(Step(assignment.Id, "proof/file"), FileForm("Photo", PngBytes()))).StatusCode);

        (await s.Agent.PostAsync(Step(assignment.Id, "start"), null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsync(Step(assignment.Id, "start"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsync(Step(assignment.Id, "accept"), null)).StatusCode);
    }

    // ---- proofs: a code, a signature, a photo --------------------------------------------------------------------

    [Fact]
    public async Task TheDeliveryCode_GoesToTheReceiversPhone_NeverToTheAgent_AndWorksOnce()
    {
        var s = await NewSceneAsync();
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);

        var sent = await s.Agent.PostAsync(Step(job.Id, "delivery-code"), null);
        var body = await sent.Content.ReadAsStringAsync();
        var code = await _delivery.LatestDeliveryCodeAsync(s.Parcel.Id);

        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.DoesNotContain(code, body, StringComparison.Ordinal);
        Assert.DoesNotContain("9851234567", body, StringComparison.Ordinal);

        var wrong = code == "000000" ? "111111" : "000000";
        var bad = await s.Agent.PostAsJsonAsync(Step(job.Id, "proof/otp"), new VerifyDeliveryCodeRequest(wrong));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Agent.PostAsJsonAsync(Step(job.Id, "proof/otp"), new VerifyDeliveryCodeRequest(code, "Sita"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(Step(job.Id, "proof/otp"), new VerifyDeliveryCodeRequest(code))).StatusCode); // used once
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(Step(job.Id, "proof/otp"), new VerifyDeliveryCodeRequest("12ab"))).StatusCode);
    }

    [Fact]
    public async Task TheDeliveryCode_IsStoredOnlyAsAHash_LocksAfterWrongGuesses_AndIsLimitedPerHour()
    {
        var s = await NewSceneAsync();
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);
        (await s.Agent.PostAsync(Step(job.Id, "delivery-code"), null)).EnsureSuccessStatusCode();
        var code = await _delivery.LatestDeliveryCodeAsync(s.Parcel.Id);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
            var stored = await db.DeliveryOtps.AsNoTracking().SingleAsync(o => o.AssignmentId == job.Id);
            Assert.DoesNotContain(code, stored.CodeHash, StringComparison.Ordinal);
            Assert.True(stored.CodeHash.Length >= 32); // a keyed hash, not the code
        }

        var wrong = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(Step(job.Id, "proof/otp"), new VerifyDeliveryCodeRequest(wrong))).StatusCode);
        }

        // Locked: even the right code no longer works. A new code has to be sent.
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(Step(job.Id, "proof/otp"), new VerifyDeliveryCodeRequest(code))).StatusCode);
        (await s.Agent.PostAsync(Step(job.Id, "delivery-code"), null)).EnsureSuccessStatusCode();
        (await s.Agent.PostAsync(Step(job.Id, "delivery-code"), null)).EnsureSuccessStatusCode();
        var tooMany = await s.Agent.PostAsync(Step(job.Id, "delivery-code"), null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        var fresh = await _delivery.LatestDeliveryCodeAsync(s.Parcel.Id);
        Assert.Equal(HttpStatusCode.OK, (await s.Agent.PostAsJsonAsync(Step(job.Id, "proof/otp"), new VerifyDeliveryCodeRequest(fresh))).StatusCode);
    }

    [Fact]
    public async Task ATrackingCode_IsNotAcceptedAsAProofOfDelivery()
    {
        var s = await NewSceneAsync();
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);

        // Anyone can ask for a tracking code, and it goes to the receiver. It must never double as a delivery code.
        (await factory.CreateClient().PostAsync(new Uri($"/api/v1/tracking/{s.Parcel.TrackingNumber}/otp", UriKind.Relative), null)).EnsureSuccessStatusCode();
        var trackingCode = await _booking.LatestOtpCodeAsync(s.Parcel.Id);
        (await s.Agent.PostAsync(Step(job.Id, "delivery-code"), null)).EnsureSuccessStatusCode();
        var deliveryCode = await _delivery.LatestDeliveryCodeAsync(s.Parcel.Id);

        if (trackingCode != deliveryCode)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(Step(job.Id, "proof/otp"), new VerifyDeliveryCodeRequest(trackingCode))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await s.Agent.PostAsJsonAsync(Step(job.Id, "proof/otp"), new VerifyDeliveryCodeRequest(deliveryCode))).StatusCode);
    }

    [Fact]
    public async Task ASignatureAndAPhoto_AreStoredPrivately_AndOnlyStaffCanSeeThem()
    {
        var s = await NewSceneAsync();
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);
        var signature = PngBytes(200);
        var photo = JpegBytes(300);

        var signed = await ReadAsync<AgentAssignmentResponse>(await s.Agent.PostAsync(Step(job.Id, "proof/file"), FileForm("Signature", signature, "Sita Gurung")));
        var photographed = await ReadAsync<AgentAssignmentResponse>(await s.Agent.PostAsync(Step(job.Id, "proof/file"), FileForm("Photo", photo, contentType: "image/jpeg", fileName: "door.jpg")));

        Assert.Equal(["Signature"], signed.Proofs.Select(p => p.Type));
        Assert.Equal(["Signature", "Photo"], photographed.Proofs.Select(p => p.Type));
        Assert.All(photographed.Proofs, p => Assert.True(p.HasFile));
        Assert.Equal("Sita Gurung", photographed.Proofs[0].RecipientName);
        Assert.True(photographed.CanComplete);

        var staff = await _booking.StaffAsync(Roles.WarehouseStaff);
        var signatureFile = await staff.GetAsync(new Uri($"/api/v1/delivery/proofs/{photographed.Proofs[0].Id}/file", UriKind.Relative));
        var photoFile = await staff.GetAsync(new Uri($"/api/v1/delivery/proofs/{photographed.Proofs[1].Id}/file", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, signatureFile.StatusCode);
        Assert.Equal("image/png", signatureFile.Content.Headers.ContentType?.MediaType);
        Assert.Equal(signature, await signatureFile.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", photoFile.Content.Headers.ContentType?.MediaType);
        Assert.Equal(photo, await photoFile.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", signatureFile.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        // Not for customers, agents, or the public; and the storage folder is not served as static files.
        var uri = new Uri($"/api/v1/delivery/proofs/{photographed.Proofs[0].Id}/file", UriKind.Relative);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _booking.CustomerAsync()).GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Agent.GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync(new Uri($"/api/v1/delivery/proofs/{Guid.NewGuid()}/file", UriKind.Relative))).StatusCode);
        var keys = Directory.EnumerateFiles(factory.ProofFolder, "*", SearchOption.AllDirectories).ToList();
        Assert.True(keys.Count >= 2);
        foreach (var key in keys.Take(3))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync(new Uri("/" + Path.GetFileName(key), UriKind.Relative))).StatusCode);
        }

        // The stored names are random: nothing the client sent (not even the file name) ends up in the path.
        Assert.All(keys, key =>
        {
            Assert.DoesNotContain("proof.png", key, StringComparison.Ordinal);
            Assert.DoesNotContain("door.jpg", key, StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{32}\\.(png|jpg|webp)$", Path.GetFileName(key));
        });
    }

    [Fact]
    public async Task ProofFiles_AreCheckedByWhatTheyAre_NotByWhatTheClientSays()
    {
        var s = await NewSceneAsync();
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);
        var gif = "GIF89a"u8.ToArray();
        var script = "<script>alert(1)</script>"u8.ToArray();
        var huge = PngBytes(7 * 1024 * 1024);

        var results = new[]
        {
            await s.Agent.PostAsync(Step(job.Id, "proof/file"), FileForm("Photo", gif, contentType: "image/png", fileName: "photo.png")),
            await s.Agent.PostAsync(Step(job.Id, "proof/file"), FileForm("Photo", script, contentType: "image/png", fileName: "x.png")),
            await s.Agent.PostAsync(Step(job.Id, "proof/file"), FileForm("Photo", [], contentType: "image/png", fileName: "empty.png")),
            await s.Agent.PostAsync(Step(job.Id, "proof/file"), FileForm("Photo", huge)),
            await s.Agent.PostAsync(Step(job.Id, "proof/file"), FileForm("Teleport", PngBytes())),
            await s.Agent.PostAsync(Step(job.Id, "proof/file"), FileForm("Otp", PngBytes())),
            await s.Agent.PostAsync(Step(job.Id, "proof/file"), FileForm("Signature", PngBytes())), // a signature needs the signer's name
            await s.Agent.PostAsync(Step(job.Id, "proof/file"), new MultipartFormDataContent { { new StringContent("Photo"), "type" } }), // no file at all
        };

        Assert.All(results, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.False((await AgentViewAsync(s.Agent, job.Id)).HasProof);
        Assert.False(Directory.Exists(factory.ProofFolder) && Directory.EnumerateFiles(factory.ProofFolder, "*", SearchOption.AllDirectories).Any(f => File.ReadAllBytes(f).Length is 6 or 25));
    }

    [Fact]
    public async Task OnlyAFewProofs_CanBeRecordedForOneDelivery()
    {
        var s = await NewSceneAsync();
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);

        for (var i = 0; i < 5; i++)
        {
            (await s.Agent.PostAsync(Step(job.Id, "proof/file"), FileForm("Photo", PngBytes()))).EnsureSuccessStatusCode();
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsync(Step(job.Id, "proof/file"), FileForm("Photo", PngBytes()))).StatusCode);
    }

    [Fact]
    public async Task ProofsOfDelivery_CanNeverBeEditedOrDeleted()
    {
        var s = await NewSceneAsync();
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);
        await _delivery.ProveByCodeAsync(s.Agent, job.Id, s.Parcel.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var proof = await db.DeliveryProofs.SingleAsync(p => p.AssignmentId == job.Id);
        db.DeliveryProofs.Remove(proof);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    // ---- cash on delivery ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ACashOnDeliveryParcel_NeedsTheExactCash_BeforeItCanBeCompleted()
    {
        var branch = await _booking.CreateBranchAsync();
        var s = await NewSceneAsync(NewBooking(branch, payment: "CashOnDelivery", cod: 2500m), branch);
        Assert.True(s.Parcel.CodAmount > 2500m); // the goods plus the delivery charge the receiver pays at the door
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);
        await _delivery.ProveByCodeAsync(s.Agent, job.Id, s.Parcel.Id);

        var early = await s.Agent.PostAsJsonAsync(Step(job.Id, "complete"), new CompleteDeliveryRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Contains("Collect the cash first", await early.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False((await AgentViewAsync(s.Agent, job.Id)).CanComplete);

        var less = await s.Agent.PostAsJsonAsync(Step(job.Id, "collect-cod"), new CollectCodRequest(s.Parcel.CodAmount - 1m));
        var more = await s.Agent.PostAsJsonAsync(Step(job.Id, "collect-cod"), new CollectCodRequest(s.Parcel.CodAmount + 1m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, less.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, more.StatusCode);
        Assert.Contains("Collect exactly", await less.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(Step(job.Id, "collect-cod"), new CollectCodRequest(0m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(Step(job.Id, "collect-cod"), new CollectCodRequest(-5m))).StatusCode);

        var collected = await ReadAsync<CodCollectionResponse>(await s.Agent.PostAsJsonAsync(Step(job.Id, "collect-cod"), new CollectCodRequest(s.Parcel.CodAmount)));
        Assert.Equal(s.Parcel.CodAmount, collected.CollectedAmount);
        Assert.Equal(s.Parcel.CodAmount, collected.ExpectedAmount);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Agent.PostAsJsonAsync(Step(job.Id, "collect-cod"), new CollectCodRequest(s.Parcel.CodAmount))).StatusCode);

        var ready = await AgentViewAsync(s.Agent, job.Id);
        Assert.True(ready.CodCollected);
        Assert.Equal(s.Parcel.CodAmount, ready.CodCollectedAmount);
        Assert.True(ready.CanComplete);
        Assert.False(ready.CanFail); // the cash is in the agent's hand: the delivery is not "failed"
        Assert.Equal(HttpStatusCode.OK, (await s.Agent.PostAsJsonAsync(Step(job.Id, "complete"), new CompleteDeliveryRequest())).StatusCode);

        var summary = await ReadAsync<AgentSummaryResponse>(await s.Agent.GetAsync(AgentMeUri));
        Assert.Equal(s.Parcel.CodAmount, summary.CodCollectedToday);
        Assert.Equal(s.Parcel.CodAmount, summary.CodCollectedUnsettled);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var row = await db.CodCollections.AsNoTracking().SingleAsync(c => c.ParcelId == s.Parcel.Id);
        Assert.Equal(s.AgentInfo.Id, row.DeliveryAgentId);
        Assert.Equal(CodCollectionStatus.Collected, row.Status);
        Assert.Equal(0m, row.Variance);
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.ParcelId == s.Parcel.Id);
        Assert.Equal(PaymentStatus.Paid, payment.Status); // the delivery charge paid at the door
    }

    [Fact]
    public async Task AParcelWithNoCashDue_RefusesACollection_AndPrepaidGoodsMoneyIsStillCollected()
    {
        var prepaid = await NewSceneAsync();
        var job = await _delivery.StartedAsync(prepaid.Admin, prepaid.Agent, prepaid.Parcel.Id, prepaid.AgentInfo.Id);
        var none = await prepaid.Agent.PostAsJsonAsync(Step(job.Id, "collect-cod"), new CollectCodRequest(100m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Contains("No cash is due", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Prepaid delivery charge, but goods money to collect: cash is due even though the charge is paid.
        var branch = await _booking.CreateBranchAsync();
        var goods = await NewSceneAsync(NewBooking(branch, payment: "Cash", cod: 800m), branch);
        Assert.Equal(800m, goods.Parcel.CodAmount);
        var goodsJob = await _delivery.StartedAsync(goods.Admin, goods.Agent, goods.Parcel.Id, goods.AgentInfo.Id);
        await _delivery.ProveByCodeAsync(goods.Agent, goodsJob.Id, goods.Parcel.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await goods.Agent.PostAsJsonAsync(Step(goodsJob.Id, "complete"), new CompleteDeliveryRequest())).StatusCode);
        (await goods.Agent.PostAsJsonAsync(Step(goodsJob.Id, "collect-cod"), new CollectCodRequest(800m))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await goods.Agent.PostAsJsonAsync(Step(goodsJob.Id, "complete"), new CompleteDeliveryRequest())).StatusCode);
    }

    [Fact]
    public async Task OnlyOneCashCollectionCanExistForAParcel_EvenIfTheDatabaseIsAskedTwice()
    {
        var branch = await _booking.CreateBranchAsync();
        var s = await NewSceneAsync(NewBooking(branch, payment: "Cash", cod: 500m), branch);
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);
        (await s.Agent.PostAsJsonAsync(Step(job.Id, "collect-cod"), new CollectCodRequest(500m))).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        db.CodCollections.Add(CodCollection.Record(s.Parcel.Id, s.AgentInfo.Id, 500m, 500m, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---- a failed delivery -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedDelivery_NeedsAReason_IsRecorded_AndKeepsTheAttempt()
    {
        var s = await NewSceneAsync();
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(Step(job.Id, "fail"), new MarkDeliveryFailedRequest(""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(Step(job.Id, "fail"), new MarkDeliveryFailedRequest("no"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(Step(job.Id, "fail"), new MarkDeliveryFailedRequest(new string('x', 201)))).StatusCode);
        Assert.Equal("OutForDelivery", (await _delivery.ParcelAsAdminAsync(s.Parcel.Id)).Status);

        var failed = await ReadAsync<AgentAssignmentResponse>(
            await s.Agent.PostAsJsonAsync(Step(job.Id, "fail"), new MarkDeliveryFailedRequest("Receiver not at home", "Called twice, no answer")));

        Assert.Equal("Failed", failed.Status);
        Assert.Equal("DeliveryFailed", failed.ParcelStatus);
        Assert.Contains("Receiver not at home", failed.FailureReason, StringComparison.Ordinal);
        Assert.False(failed.CanComplete);
        Assert.False(failed.CanFail);
        Assert.True(failed.CanReturnToWarehouse);

        var parcel = await _delivery.ParcelAsAdminAsync(s.Parcel.Id);
        Assert.Equal("DeliveryFailed", parcel.Status);
        Assert.Contains("Receiver not at home", parcel.History[^1].Remarks, StringComparison.Ordinal);
        Assert.Contains("Called twice", parcel.History[^1].Remarks, StringComparison.Ordinal);

        // It cannot be completed afterwards, and the failed attempt stays on the agent's open list until the parcel is handed in.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsJsonAsync(Step(job.Id, "complete"), new CompleteDeliveryRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsJsonAsync(Step(job.Id, "fail"), new MarkDeliveryFailedRequest("Failing again"))).StatusCode);
        var open = await ReadAsync<PagedResponse<AgentAssignmentResponse>>(await s.Agent.GetAsync(new Uri("/api/v1/agent/assignments", UriKind.Relative)));
        var listed = Assert.Single(open.Items);
        Assert.True(listed.CanReturnToWarehouse);
        Assert.Null(listed.ReceiverPhone);

        var summary = await ReadAsync<AgentSummaryResponse>(await s.Agent.GetAsync(AgentMeUri));
        Assert.Equal(1, summary.FailedToday);
        Assert.Equal(0, summary.OpenAssignments);
    }

    [Fact]
    public async Task AFailedParcel_CanBeTakenBackToAWarehouse_OnlyOnce()
    {
        var s = await NewSceneAsync();
        var warehouse = await _booking.CreateWarehouseAsync(s.Branch, "Return Hub");
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);

        var tooSoon = await s.Agent.PostAsJsonAsync(Step(job.Id, "return-to-warehouse"), new ReturnToWarehouseRequest(warehouse));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooSoon.StatusCode);
        (await s.Agent.PostAsJsonAsync(Step(job.Id, "fail"), new MarkDeliveryFailedRequest("Address could not be found"))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Agent.PostAsJsonAsync(Step(job.Id, "return-to-warehouse"), new ReturnToWarehouseRequest(Guid.Empty))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Agent.PostAsJsonAsync(Step(job.Id, "return-to-warehouse"), new ReturnToWarehouseRequest(Guid.NewGuid()))).StatusCode);

        var back = await ReadAsync<AgentAssignmentResponse>(
            await s.Agent.PostAsJsonAsync(Step(job.Id, "return-to-warehouse"), new ReturnToWarehouseRequest(warehouse, "Left with the duty officer")));

        Assert.Equal("ArrivedAtWarehouse", back.ParcelStatus);
        Assert.False(back.CanReturnToWarehouse);
        var parcel = await _delivery.ParcelAsAdminAsync(s.Parcel.Id);
        Assert.Equal("ArrivedAtWarehouse", parcel.Status);
        Assert.Equal("Left with the duty officer", parcel.History[^1].Remarks);
        Assert.Equal("Return Hub", parcel.History[^1].Location);

        var movements = await ReadAsync<List<ParcelMovementResponse>>(await s.Admin.GetAsync(new Uri($"/api/v1/parcels/{s.Parcel.Id}/movements", UriKind.Relative)));
        Assert.Equal(["OutForDelivery", "Arrival"], movements.Select(m => m.ScanType));
        Assert.Equal("Return Hub", movements[^1].WarehouseName);

        // Not twice, and no longer on the open list.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsJsonAsync(Step(job.Id, "return-to-warehouse"), new ReturnToWarehouseRequest(warehouse))).StatusCode);
        var open = await ReadAsync<PagedResponse<AgentAssignmentResponse>>(await s.Agent.GetAsync(new Uri("/api/v1/agent/assignments", UriKind.Relative)));
        Assert.Empty(open.Items);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var handIn = await db.DeliveryAssignments.AsNoTracking().SingleAsync(a => a.ParcelId == s.Parcel.Id && a.Type == AssignmentType.Return);
        Assert.Equal(AssignmentStatus.Completed, handIn.Status);
        Assert.Equal(s.AgentInfo.Id, handIn.DeliveryAgentId);
    }

    [Fact]
    public async Task AnAgent_CannotReturnAParcelToAWarehouseOfAnotherBranch_OrASwitchedOffOne()
    {
        var s = await NewSceneAsync();
        var otherBranch = await _booking.CreateBranchAsync();
        var foreign = await _booking.CreateWarehouseAsync(otherBranch, "Foreign Hub");
        var own = await _booking.CreateWarehouseAsync(s.Branch, "Own Hub");
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);
        (await s.Agent.PostAsJsonAsync(Step(job.Id, "fail"), new MarkDeliveryFailedRequest("Nobody answered the door"))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Agent.PostAsJsonAsync(Step(job.Id, "return-to-warehouse"), new ReturnToWarehouseRequest(foreign))).StatusCode);
        (await s.Admin.PostAsync(new Uri($"/api/v1/warehouses/{own}/deactivate", UriKind.Relative), null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Agent.PostAsJsonAsync(Step(job.Id, "return-to-warehouse"), new ReturnToWarehouseRequest(own))).StatusCode);
        Assert.Equal("DeliveryFailed", (await _delivery.ParcelAsAdminAsync(s.Parcel.Id)).Status);
    }

    [Fact]
    public async Task AFailedParcel_CanBeGivenOutAgain_AndTheNewAttemptIsCountedAndSucceeds()
    {
        var s = await NewSceneAsync();
        var (secondClient, second, _, _) = await _delivery.NewAgentAsync(s.Branch);
        var first = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);
        (await s.Agent.PostAsJsonAsync(Step(first.Id, "fail"), new MarkDeliveryFailedRequest("Receiver asked to come tomorrow"))).EnsureSuccessStatusCode();

        var ready = await ReadAsync<PagedResponse<ReadyParcelResponse>>(await s.Admin.GetAsync(new Uri($"{ReadyUri}?branchId={s.Branch}&search={s.Parcel.TrackingNumber}", UriKind.Relative)));
        var row = Assert.Single(ready.Items);
        Assert.Equal("DeliveryFailed", row.Status);
        Assert.Equal(1, row.FailedAttempts);

        var again = await _delivery.StartedAsync(s.Admin, secondClient, s.Parcel.Id, second.Id);
        Assert.Equal(2, again.AttemptNumber);
        await _delivery.ProveByCodeAsync(secondClient, again.Id, s.Parcel.Id);
        var done = await ReadAsync<AgentAssignmentResponse>(await secondClient.PostAsJsonAsync(Step(again.Id, "complete"), new CompleteDeliveryRequest()));

        Assert.Equal("Delivered", done.ParcelStatus);
        Assert.Equal(["OutForDelivery", "DeliveryFailed", "OutForDelivery", "Delivered"], (await _delivery.ParcelAsAdminAsync(s.Parcel.Id)).History.TakeLast(4).Select(h => h.Status));

        // The first agent's old failed attempt cannot be used to move a parcel that has since been delivered.
        var stale = await s.Agent.PostAsJsonAsync(Step(first.Id, "return-to-warehouse"), new ReturnToWarehouseRequest(await _booking.CreateWarehouseAsync(s.Branch)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
    }

    [Fact]
    public async Task OnceAFailedParcelIsGivenToAnotherAgent_TheFirstAgentCanNoLongerTakeItBack()
    {
        var s = await NewSceneAsync();
        var (_, second, _, _) = await _delivery.NewAgentAsync(s.Branch);
        var warehouse = await _booking.CreateWarehouseAsync(s.Branch);
        var first = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);
        (await s.Agent.PostAsJsonAsync(Step(first.Id, "fail"), new MarkDeliveryFailedRequest("Receiver not at home"))).EnsureSuccessStatusCode();

        await _delivery.AssignAsync(s.Admin, s.Parcel.Id, second.Id);

        var refused = await s.Agent.PostAsJsonAsync(Step(first.Id, "return-to-warehouse"), new ReturnToWarehouseRequest(warehouse));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("given out again", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var open = await ReadAsync<PagedResponse<AgentAssignmentResponse>>(await s.Agent.GetAsync(new Uri("/api/v1/agent/assignments", UriKind.Relative)));
        Assert.Empty(open.Items);
        Assert.False((await AgentViewAsync(s.Agent, first.Id)).CanReturnToWarehouse);
    }

    // ---- the assignment follows the parcel ---------------------------------------------------------------------------

    [Fact]
    public async Task WhenAManagerChangesTheStatusDirectly_TheAgentsAssignmentFollows()
    {
        var s = await NewSceneAsync();
        var job = await _delivery.AssignAsync(s.Admin, s.Parcel.Id, s.AgentInfo.Id);

        (await s.Admin.PostAsJsonAsync(StatusUri(s.Parcel.Id), To("OutForDelivery", warehouseId: null, branchId: s.Branch))).EnsureSuccessStatusCode();
        Assert.Equal("InProgress", (await AgentViewAsync(s.Agent, job.Id)).Status);

        (await s.Admin.PostAsJsonAsync(StatusUri(s.Parcel.Id), To("DeliveryFailed", "Manager marked it failed"))).EnsureSuccessStatusCode();
        var failed = await AgentViewAsync(s.Agent, job.Id);
        Assert.Equal("Failed", failed.Status);
        Assert.Contains("Manager marked it failed", failed.FailureReason, StringComparison.Ordinal);

        var next = await _delivery.AssignAsync(s.Admin, s.Parcel.Id, s.AgentInfo.Id);
        (await s.Admin.PostAsJsonAsync(StatusUri(s.Parcel.Id), To("OutForDelivery", warehouseId: null, branchId: s.Branch))).EnsureSuccessStatusCode();
        (await s.Admin.PostAsJsonAsync(StatusUri(s.Parcel.Id), To("Delivered", "Manager confirmed"))).EnsureSuccessStatusCode();
        Assert.Equal("Completed", (await AgentViewAsync(s.Agent, next.Id)).Status);
    }

    [Fact]
    public async Task HandingTheParcelToTheRiderAtTheWarehouse_StartsTheAssignment()
    {
        var s = await NewSceneAsync();
        var job = await _delivery.AssignAsync(s.Admin, s.Parcel.Id, s.AgentInfo.Id);
        var warehouse = await ParcelWarehouseAsync(s.Parcel.Id);

        var scan = await s.Admin.PostAsJsonAsync(new Uri("/api/v1/scans", UriKind.Relative), new ScanRequest(s.Parcel.TrackingNumber, "OutForDelivery", warehouse));

        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        Assert.Equal("InProgress", (await AgentViewAsync(s.Agent, job.Id)).Status);
    }

    [Fact]
    public async Task ParcelsMovingBetweenWarehouses_LeaveAWaitingAssignmentAlone()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (agent, info, _, _) = await _delivery.NewAgentAsync(branch);
        var customer = await _booking.CustomerAsync();
        var parcel = await BookAsync(customer, NewBooking(branch));
        await _booking.AdvanceAsync(parcel.Id, ParcelStatusCode.PickedUp, ParcelStatusCode.Shipped, ParcelStatusCode.InTransit);
        var warehouse = await _booking.CreateWarehouseAsync(branch);
        var job = await _delivery.AssignAsync(admin, parcel.Id, info.Id);

        (await admin.PostAsJsonAsync(new Uri("/api/v1/scans", UriKind.Relative), new ScanRequest(parcel.TrackingNumber, "Arrival", warehouse))).EnsureSuccessStatusCode();

        var view = await AgentViewAsync(agent, job.Id);
        Assert.Equal("Assigned", view.Status);
        Assert.Equal("ArrivedAtWarehouse", view.ParcelStatus);
        Assert.True(view.CanStart);
    }

    // ---- what the agent is shown ---------------------------------------------------------------------------------

    [Fact]
    public async Task TheAgent_SeesThePhoneAndAddressOnlyWhileTheJobIsOpen()
    {
        var branch = await _booking.CreateBranchAsync();
        var s = await NewSceneAsync(NewBooking(branch, receiverPhone: "9861112233", receiverName: "Sita Gurung"), branch);
        var job = await _delivery.AssignAsync(s.Admin, s.Parcel.Id, s.AgentInfo.Id);

        var open = await AgentViewAsync(s.Agent, job.Id);
        Assert.Equal("9861112233", open.ReceiverPhone);
        Assert.Contains("Lakeside", open.ReceiverAddress, StringComparison.Ordinal);
        Assert.Equal("Blue gate", open.ReceiverInstructions);

        (await s.Agent.PostAsync(Step(job.Id, "start"), null)).EnsureSuccessStatusCode();
        await _delivery.ProveByCodeAsync(s.Agent, job.Id, s.Parcel.Id);
        (await s.Agent.PostAsJsonAsync(Step(job.Id, "complete"), new CompleteDeliveryRequest())).EnsureSuccessStatusCode();

        var json = await (await s.Agent.GetAsync(AgentAssignmentUri(job.Id))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("9861112233", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Lakeside", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Blue gate", json, StringComparison.Ordinal);
        var list = await (await s.Agent.GetAsync(new Uri("/api/v1/agent/assignments?scope=history", UriKind.Relative))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("9861112233", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAgentList_IsPaged_AndValidatesItsScope()
    {
        var branch = await _booking.CreateBranchAsync();
        var admin = await _delivery.AdminAsync();
        var (agent, info, _, _) = await _delivery.NewAgentAsync(branch);
        for (var i = 0; i < 3; i++)
        {
            await _delivery.AssignAsync(admin, (await _delivery.ReadyParcelAsync(branch)).Id, info.Id);
        }

        var page = await ReadAsync<PagedResponse<AgentAssignmentResponse>>(await agent.GetAsync(new Uri("/api/v1/agent/assignments?pageSize=2", UriKind.Relative)));
        var second = await ReadAsync<PagedResponse<AgentAssignmentResponse>>(await agent.GetAsync(new Uri("/api/v1/agent/assignments?pageSize=2&page=2", UriKind.Relative)));

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Single(second.Items);
        Assert.True(page.Items[0].AssignedAtUtc <= page.Items[1].AssignedAtUtc); // oldest job first
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.GetAsync(new Uri("/api/v1/agent/assignments?scope=everything", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.GetAsync(new Uri("/api/v1/agent/assignments?pageSize=1000", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task DeliveryEvents_AreWrittenToTheAuditTrail_WithoutTheCodeOrTheCash()
    {
        var branch = await _booking.CreateBranchAsync();
        var s = await NewSceneAsync(NewBooking(branch, payment: "Cash", cod: 900m), branch);
        var job = await _delivery.StartedAsync(s.Admin, s.Agent, s.Parcel.Id, s.AgentInfo.Id);
        (await s.Agent.PostAsJsonAsync(Step(job.Id, "collect-cod"), new CollectCodRequest(900m))).EnsureSuccessStatusCode();
        await _delivery.ProveByCodeAsync(s.Agent, job.Id, s.Parcel.Id);
        var code = await _delivery.LatestDeliveryCodeAsync(s.Parcel.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var entries = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == job.Id.ToString()).ToListAsync();

        Assert.Contains(entries, e => e.Action == "DeliveryCodeSent");
        Assert.Contains(entries, e => e.Action == "DeliveryProofRecorded");
        Assert.Contains(entries, e => e.Action == "CodCollected");
        Assert.All(entries, e => Assert.DoesNotContain(code, e.NewValues ?? string.Empty, StringComparison.Ordinal));
        Assert.All(entries.Where(e => e.Action.StartsWith("Delivery", StringComparison.Ordinal) || e.Action == "CodCollected"), e => Assert.NotNull(e.UserId));
    }

    private async Task<Guid> ParcelWarehouseAsync(Guid parcelId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        return (await db.Parcels.AsNoTracking().SingleAsync(p => p.Id == parcelId)).CurrentWarehouseId!.Value;
    }
}
