using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Contracts.Auth;
using NepalParcel.Domain.Auditing;

namespace NepalParcel.Application.Auth;

internal sealed partial class UserAdministrationService(
    IIdentityService identity,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<CreateStaffUserRequest> validator,
    ILogger<UserAdministrationService> logger) : IUserAdministrationService
{
    public async Task<CurrentUserResponse> CreateStaffUserAsync(CreateStaffUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await validator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var created = await identity
            .CreateUserAsync(request.FullName.Trim(), request.Email.Trim(), request.PhoneNumber, request.Password, request.Role, cancellationToken)
            .ConfigureAwait(false);
        if (!created.Succeeded)
        {
            throw created.Failures.ToException();
        }

        var user = created.User!;
        auditLogs.Add(AuditLog.Create(
            AuditActions.UserCreated,
            "User",
            user.Id.ToString(),
            currentUser.UserId,
            clock.GetUtcNow(),
            newValues: JsonSerializer.Serialize(new { role = request.Role }),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogStaffCreated(logger, user.Id, request.Role, currentUser.UserId);
        return user.ToResponse();
    }

    [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "Staff user {UserId} created with role {Role} by {ActorId}")]
    private static partial void LogStaffCreated(ILogger logger, Guid userId, string role, Guid? actorId);
}
