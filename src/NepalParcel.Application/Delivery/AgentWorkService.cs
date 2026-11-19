using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Notifications;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Network;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Payments;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Application.Delivery;

/// <summary>
/// What a delivery agent does on the road. Every operation first resolves the caller to an active agent profile and then
/// loads the assignment <em>of that agent</em>: anyone else's assignment is answered with "not found", so an agent cannot tell
/// that a parcel exists, let alone read or change it. Every change to the parcel goes through <see cref="ParcelStatusChanger"/>
/// so the workflow, the history and the audit trail are the same as everywhere else.
/// </summary>
internal sealed partial class AgentWorkService(
    AgentContext context,
    IDeliveryAssignmentRepository assignments,
    IDeliveryAgentRepository agents,
    IBranchRepository branches,
    IDeliveryProofRepository proofs,
    ICodCollectionRepository cods,
    IDeliveryOtpRepository otps,
    IParcelRepository parcels,
    IPaymentRepository payments,
    PaymentLedger ledger,
    INotificationOutboxRepository outbox,
    IProofStorage storage,
    ITrackingSecurity security,
    ParcelStatusChanger changer,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IOptions<TrackingOptions> trackingOptions,
    IOptions<DeliveryOptions> deliveryOptions,
    IValidator<AgentAssignmentQuery> queryValidator,
    IValidator<MarkDeliveryFailedRequest> failValidator,
    IValidator<CollectCodRequest> codValidator,
    IValidator<VerifyDeliveryCodeRequest> codeValidator,
    IValidator<CompleteDeliveryRequest> completeValidator,
    IValidator<ReturnToWarehouseRequest> returnValidator,
    ILogger<AgentWorkService> logger) : IAgentWorkService
{
    private const int CodeLength = 6;
    private const string OtpTemplateKey = "delivery-otp";

    // The code is bound to the parcel and to this purpose, so a tracking code can never be used as a delivery code.
    private static string Salted(string code) => "delivery:" + code;

    // ---- the agent's own screens ---------------------------------------------------------------------------------------

    public async Task<AgentSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var agent = await context.RequireAgentAsync(cancellationToken).ConfigureAwait(false);
        return await SummaryAsync(agent, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentSummaryResponse> SetAvailabilityAsync(SetAvailabilityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = await context.RequireAgentAsync(cancellationToken).ConfigureAwait(false);
        agent.SetAvailability(request.IsAvailable);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await SummaryAsync(agent, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedResponse<AgentAssignmentResponse>> GetAssignmentsAsync(AgentAssignmentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await queryValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var agent = await context.RequireAgentAsync(cancellationToken).ConfigureAwait(false);
        var open = !string.Equals(query.Scope, DeliveryRules.HistoryScope, StringComparison.OrdinalIgnoreCase);
        var (items, total) = await assignments.GetForAgentAsync(agent.Id, open, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return new PagedResponse<AgentAssignmentResponse>([], query.Page, query.PageSize, total);
        }

        var parcelMap = await parcels.GetByIdsAsync(items.Select(a => a.ParcelId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var proofMap = await proofs.GetByAssignmentsAsync(items.Select(a => a.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var codMap = await cods.GetByParcelsAsync(parcelMap.Keys.ToList(), cancellationToken).ConfigureAwait(false);

        return new PagedResponse<AgentAssignmentResponse>(
            items.Select(a => a.ToAgentResponse(
                parcelMap[a.ParcelId],
                codMap.GetValueOrDefault(a.ParcelId),
                proofMap.TryGetValue(a.Id, out var list) ? list : [],
                returnPending: open && a.Status == AssignmentStatus.Failed)).ToList(),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<AgentAssignmentResponse> GetAssignmentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var (_, assignment, parcel) = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        return await BuildAsync(assignment, parcel, cancellationToken).ConfigureAwait(false);
    }

    // ---- the delivery ----------------------------------------------------------------------------------------------------

    public async Task<AgentAssignmentResponse> AcceptAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var (_, assignment, parcel) = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        assignment.Accept();
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await BuildAsync(assignment, parcel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentAssignmentResponse> StartAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var (agent, assignment, parcel) = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (assignment.Status is not (AssignmentStatus.Assigned or AssignmentStatus.Accepted))
        {
            throw new DomainException($"This delivery is '{assignment.Status}', so it cannot be started.");
        }

        // The parcel leaves the warehouse (or branch) it is in; if it is on a vehicle, it is recorded at the agent's branch.
        Guid? warehouseId = parcel.CurrentWarehouseId;
        Guid? branchId = warehouseId is null ? parcel.CurrentBranchId ?? agent.BranchId : null;

        await changer
            .ApplyAsync(parcel, ParcelStatusCode.OutForDelivery, $"Out for delivery with {agent.FullName}", branchId, warehouseId, null, cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogStarted(logger, assignment.Id, parcel.Id);
        return await BuildAsync(assignment, parcel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentAssignmentResponse> FailAsync(Guid id, MarkDeliveryFailedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await failValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var (_, assignment, parcel) = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        RequireInProgress(assignment, parcel, "marked as failed");
        if (await cods.GetByParcelAsync(parcel.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new DomainException("Cash has already been collected for this parcel, so the delivery cannot be marked as failed. Finish the delivery, or ask a manager for help.");
        }

        var reason = request.Reason.Trim();
        var remarks = string.IsNullOrWhiteSpace(request.Remarks) ? reason : $"{reason}. {request.Remarks.Trim()}";
        await changer.ApplyAsync(parcel, ParcelStatusCode.DeliveryFailed, remarks, null, null, null, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogFailed(logger, assignment.Id, parcel.Id);
        return await BuildAsync(assignment, parcel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeliveryCodeSentResponse> SendDeliveryCodeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var (_, assignment, parcel) = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        RequireInProgress(assignment, parcel, "given a delivery code");

        var settings = trackingOptions.Value;
        var now = clock.GetUtcNow();
        var recent = await otps.CountSinceAsync(assignment.Id, now.AddHours(-1), cancellationToken).ConfigureAwait(false);
        if (recent >= settings.MaxRequestsPerHour)
        {
            throw new DomainException("Too many codes have been sent for this delivery. Wait a while, or use a signature or a photo as the proof.");
        }

        var code = NewCode();
        otps.Add(DeliveryOtp.Create(parcel.Id, assignment.Id, security.HashOtp(parcel.Id, Salted(code)), now, TimeSpan.FromMinutes(settings.OtpLifetimeMinutes)));
        outbox.Add(NotificationOutboxMessage.Enqueue(
            NotificationChannel.Sms,
            parcel.ReceiverPhone,
            OtpTemplateKey,
            JsonSerializer.Serialize(new { code, trackingNumber = parcel.TrackingNumber, validForMinutes = settings.OtpLifetimeMinutes }),
            now,
            parcel.Id));
        AddAudit(AuditActions.DeliveryCodeSent, assignment.Id, now, new { parcelId = parcel.Id });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (settings.LogCodes)
        {
            LogDevelopmentCode(logger, parcel.TrackingNumber, code);
        }

        return new DeliveryCodeSentResponse("A code has been sent to the receiver's phone. Ask them to read it out to you.", settings.OtpLifetimeMinutes);
    }

    public async Task<AgentAssignmentResponse> VerifyDeliveryCodeAsync(Guid id, VerifyDeliveryCodeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await codeValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var (agent, assignment, parcel) = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        RequireInProgress(assignment, parcel, "given a proof");

        var settings = trackingOptions.Value;
        var now = clock.GetUtcNow();
        var failure = new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Code"] = ["The code is incorrect or has expired. Send a new code and try again."],
        });

        var otp = await otps.GetLatestAsync(assignment.Id, cancellationToken).ConfigureAwait(false);
        if (otp is null || !otp.IsUsable(now, settings.MaxFailedAttempts))
        {
            throw failure;
        }

        if (!security.VerifyOtp(parcel.Id, Salted(request.Code), otp.CodeHash))
        {
            otp.RegisterFailedAttempt();
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw failure;
        }

        await EnsureRoomForProofAsync(assignment, cancellationToken).ConfigureAwait(false);
        otp.Consume(now);
        proofs.Add(DeliveryProof.Create(parcel.Id, ProofType.Otp, now, assignment.Id, agent.Id, null, request.RecipientName?.Trim(), otpVerified: true));
        AddAudit(AuditActions.DeliveryProofRecorded, assignment.Id, now, new { parcelId = parcel.Id, type = nameof(ProofType.Otp) });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await BuildAsync(assignment, parcel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentAssignmentResponse> RecordFileProofAsync(
        Guid id,
        string type,
        string? recipientName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ProofType>(type, ignoreCase: true, out var proofType) || proofType is not (ProofType.Signature or ProofType.Photo) || int.TryParse(type, out _))
        {
            throw Invalid("Type", "Type must be Signature or Photo.");
        }

        var name = recipientName?.Trim();
        if (name is { Length: > 150 })
        {
            throw Invalid("RecipientName", "The name can have at most 150 characters.");
        }

        if (proofType == ProofType.Signature && string.IsNullOrWhiteSpace(name))
        {
            throw Invalid("RecipientName", "Write the name of the person who signed.");
        }

        var maxBytes = deliveryOptions.Value.MaxProofSizeKb * 1024;
        if (content.Length == 0)
        {
            throw Invalid("File", "Choose a file.");
        }

        if (content.Length > maxBytes)
        {
            throw Invalid("File", $"The file is too large. The limit is {deliveryOptions.Value.MaxProofSizeKb / 1024.0:0.#} MB.");
        }

        var extension = ProofImage.DetectExtension(content.Span)
            ?? throw Invalid("File", "The file must be a PNG, JPEG or WebP picture.");

        var (agent, assignment, parcel) = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        RequireInProgress(assignment, parcel, "given a proof");
        await EnsureRoomForProofAsync(assignment, cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var key = await storage.SaveAsync(content, extension, cancellationToken).ConfigureAwait(false);
        try
        {
            proofs.Add(DeliveryProof.Create(parcel.Id, proofType, now, assignment.Id, agent.Id, key, name));
            AddAudit(AuditActions.DeliveryProofRecorded, assignment.Id, now, new { parcelId = parcel.Id, type = proofType.ToString() });
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Do not leave a picture behind that no proof points at.
            await storage.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return await BuildAsync(assignment, parcel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodCollectionResponse> CollectCodAsync(Guid id, CollectCodRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await codValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var (agent, assignment, parcel) = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        RequireInProgress(assignment, parcel, "used to collect cash");
        if (!parcel.IsCod)
        {
            throw new DomainException("No cash is due for this parcel.");
        }

        if (await cods.GetByParcelAsync(parcel.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new ConflictException("The cash for this parcel has already been collected.");
        }

        // Exactly what is due, no more and no less. Anything else is for a manager to sort out, not for the road.
        if (request.CollectedAmount != parcel.CodAmount)
        {
            throw new DomainException(
                string.Create(CultureInfo.InvariantCulture, $"Collect exactly NPR {parcel.CodAmount:0.00}. You entered NPR {request.CollectedAmount:0.00}."));
        }

        var now = clock.GetUtcNow();
        var collection = CodCollection.Record(parcel.Id, agent.Id, parcel.CodAmount, request.CollectedAmount, now);
        cods.Add(collection);

        // If the receiver was also paying the delivery charge at the door, that charge is now paid.
        var payment = await payments.GetLatestByParcelIdAsync(parcel.Id, cancellationToken).ConfigureAwait(false);
        if (payment is { Status: PaymentStatus.Pending, Method: PaymentMethod.CashOnDelivery })
        {
            payment.MarkPaid(now, $"COD {parcel.TrackingNumber}");
            ledger.RecordManualSuccess(payment, "CashOnDelivery", now, currentUser.UserId);
        }

        AddAudit(AuditActions.CodCollected, assignment.Id, now, new { parcelId = parcel.Id, amount = request.CollectedAmount });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogCodCollected(logger, assignment.Id, parcel.Id, agent.Id);
        return new CodCollectionResponse(collection.Id, parcel.Id, collection.ExpectedAmount, collection.CollectedAmount, collection.CollectedAtUtc);
    }

    public async Task<AgentAssignmentResponse> CompleteAsync(Guid id, CompleteDeliveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await completeValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var (_, assignment, parcel) = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        RequireInProgress(assignment, parcel, "completed");

        if (await proofs.CountForAssignmentAsync(assignment.Id, cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new DomainException("Record a proof of delivery first: a code from the receiver, a signature or a photo.");
        }

        if (parcel.IsCod && await cods.GetByParcelAsync(parcel.Id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new DomainException(string.Create(CultureInfo.InvariantCulture, $"Collect the cash first: NPR {parcel.CodAmount:0.00} is due."));
        }

        var remarks = string.IsNullOrWhiteSpace(request.Remarks) ? "Delivered" : request.Remarks.Trim();
        await changer.ApplyAsync(parcel, ParcelStatusCode.Delivered, remarks, null, null, null, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogDelivered(logger, assignment.Id, parcel.Id);
        return await BuildAsync(assignment, parcel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentAssignmentResponse> ReturnToWarehouseAsync(Guid id, ReturnToWarehouseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await returnValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var (agent, assignment, parcel) = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (assignment.Status != AssignmentStatus.Failed || parcel.Status != ParcelStatusCode.DeliveryFailed)
        {
            throw new DomainException("Only a parcel whose delivery has failed can be returned to a warehouse.");
        }

        if (await assignments.HasLaterAttemptAsync(parcel.Id, assignment.AttemptNumber, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException("This parcel has been given out again, so it cannot be returned from this attempt.");
        }

        var now = clock.GetUtcNow();
        var remarks = string.IsNullOrWhiteSpace(request.Remarks) ? $"Returned to the warehouse by {agent.FullName}" : request.Remarks.Trim();

        // Handing the parcel in is an arrival at that warehouse: the status, the movement history and the audit trail all get it.
        await changer.ApplyAsync(parcel, ParcelStatusCode.ArrivedAtWarehouse, remarks, null, request.WarehouseId, null, cancellationToken).ConfigureAwait(false);

        // And the hand-in is kept as a task of its own, finished at once, so the agent's record shows it.
        var attempt = await assignments.GetLastAttemptNumberAsync(parcel.Id, AssignmentType.Return, cancellationToken).ConfigureAwait(false) + 1;
        var handIn = DeliveryAssignment.Assign(parcel.Id, agent.Id, AssignmentType.Return, now, currentUser.UserId, attempt);
        handIn.Start(now);
        handIn.Complete(now);
        assignments.Add(handIn);

        AddAudit(AuditActions.ParcelReturnedToWarehouse, assignment.Id, now, new { parcelId = parcel.Id, warehouseId = request.WarehouseId });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogReturned(logger, assignment.Id, parcel.Id, request.WarehouseId);
        return await BuildAsync(assignment, parcel, cancellationToken).ConfigureAwait(false);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    /// <summary>The caller's own delivery assignment and its parcel. Anyone else's assignment looks like it does not exist.</summary>
    private async Task<(DeliveryAgent Agent, DeliveryAssignment Assignment, Parcel Parcel)> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var agent = await context.RequireAgentAsync(cancellationToken).ConfigureAwait(false);
        var assignment = await assignments.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (assignment is null || assignment.DeliveryAgentId != agent.Id || assignment.Type != AssignmentType.Delivery)
        {
            throw new NotFoundException("Delivery assignment", id);
        }

        var parcel = await parcels.GetByIdAsync(assignment.ParcelId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Delivery assignment", id);
        return (agent, assignment, parcel);
    }

    private static void RequireInProgress(DeliveryAssignment assignment, Parcel parcel, string what)
    {
        if (assignment.Status != AssignmentStatus.InProgress || parcel.Status != ParcelStatusCode.OutForDelivery)
        {
            throw new DomainException($"Only a delivery that is out for delivery can be {what}. This one is '{assignment.Status}'.");
        }
    }

    private async Task EnsureRoomForProofAsync(DeliveryAssignment assignment, CancellationToken cancellationToken)
    {
        var max = deliveryOptions.Value.MaxProofsPerAssignment;
        if (await proofs.CountForAssignmentAsync(assignment.Id, cancellationToken).ConfigureAwait(false) >= max)
        {
            throw new DomainException($"At most {max} proofs can be recorded for one delivery.");
        }
    }

    private async Task<AgentAssignmentResponse> BuildAsync(DeliveryAssignment assignment, Parcel parcel, CancellationToken cancellationToken)
    {
        var cod = await cods.GetByParcelAsync(parcel.Id, cancellationToken).ConfigureAwait(false);
        var list = await proofs.GetByAssignmentAsync(assignment.Id, cancellationToken).ConfigureAwait(false);
        var returnPending = assignment.Status == AssignmentStatus.Failed
                            && parcel.Status == ParcelStatusCode.DeliveryFailed
                            && !await assignments.HasLaterAttemptAsync(parcel.Id, assignment.AttemptNumber, cancellationToken).ConfigureAwait(false);
        return assignment.ToAgentResponse(parcel, cod, list, returnPending);
    }

    private async Task<AgentSummaryResponse> SummaryAsync(DeliveryAgent agent, CancellationToken cancellationToken)
    {
        var branch = (await branches.GetByIdsAsync([agent.BranchId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(agent.BranchId);
        var open = (await agents.CountOpenAssignmentsAsync([agent.Id], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(agent.Id);
        var dayStart = NepalDay.StartUtc(clock.GetUtcNow());
        var (delivered, failed) = await assignments.CountFinishedSinceAsync(agent.Id, dayStart, cancellationToken).ConfigureAwait(false);
        return new AgentSummaryResponse(
            agent.Id,
            agent.FullName,
            branch?.Name ?? string.Empty,
            agent.IsAvailable,
            open,
            delivered,
            failed,
            await cods.SumCollectedSinceAsync(agent.Id, dayStart, cancellationToken).ConfigureAwait(false),
            await cods.SumUnsettledAsync(agent.Id, cancellationToken).ConfigureAwait(false));
    }

    private static RequestValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    private void AddAudit(string action, Guid assignmentId, DateTimeOffset now, object values) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "DeliveryAssignment",
            assignmentId.ToString(),
            currentUser.UserId,
            now,
            newValues: JsonSerializer.Serialize(values),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

    private static string NewCode() =>
        RandomNumberGenerator.GetInt32(0, (int)Math.Pow(10, CodeLength)).ToString(new string('0', CodeLength), CultureInfo.InvariantCulture);

    [LoggerMessage(EventId = 9421, Level = LogLevel.Information, Message = "Delivery {AssignmentId}: parcel {ParcelId} is out for delivery")]
    private static partial void LogStarted(ILogger logger, Guid assignmentId, Guid parcelId);

    [LoggerMessage(EventId = 9422, Level = LogLevel.Information, Message = "Delivery {AssignmentId}: attempt for parcel {ParcelId} failed")]
    private static partial void LogFailed(ILogger logger, Guid assignmentId, Guid parcelId);

    [LoggerMessage(EventId = 9423, Level = LogLevel.Information, Message = "Delivery {AssignmentId}: parcel {ParcelId} delivered")]
    private static partial void LogDelivered(ILogger logger, Guid assignmentId, Guid parcelId);

    [LoggerMessage(EventId = 9424, Level = LogLevel.Information, Message = "Delivery {AssignmentId}: cash for parcel {ParcelId} collected by agent {AgentId}")]
    private static partial void LogCodCollected(ILogger logger, Guid assignmentId, Guid parcelId, Guid agentId);

    [LoggerMessage(EventId = 9425, Level = LogLevel.Information, Message = "Delivery {AssignmentId}: parcel {ParcelId} returned to warehouse {WarehouseId}")]
    private static partial void LogReturned(ILogger logger, Guid assignmentId, Guid parcelId, Guid warehouseId);

    [LoggerMessage(EventId = 9426, Level = LogLevel.Warning, Message = "DEVELOPMENT ONLY: delivery code for {TrackingNumber} is {Code}")]
    private static partial void LogDevelopmentCode(ILogger logger, string trackingNumber, string code);
}
