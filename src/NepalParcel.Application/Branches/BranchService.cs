using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Mapping;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;
using NepalParcel.Domain.Branches;

namespace NepalParcel.Application.Branches;

internal sealed partial class BranchService(
    IBranchRepository repository,
    IUnitOfWork unitOfWork,
    IValidator<CreateBranchRequest> createValidator,
    IValidator<PageQuery> pageValidator,
    ILogger<BranchService> logger) : IBranchService
{
    public async Task<PagedResponse<BranchResponse>> GetPageAsync(PageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await pageValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var (items, total) = await repository.GetPageAsync(query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<BranchResponse>(
            items.Select(b => b.ToResponse()).ToList(),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<BranchResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var branch = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Branch", id);

        return branch.ToResponse();
    }

    public async Task<BranchResponse> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await createValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var code = request.Code.Trim().ToUpperInvariant();
        if (await repository.CodeExistsAsync(code, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException($"A branch with code '{code}' already exists.");
        }

        var branch = Branch.Create(
            code,
            request.Name,
            Enum.Parse<BranchType>(request.Type, ignoreCase: true),
            request.Address.ToDomain(),
            request.PhoneNumber,
            request.Email);

        repository.Add(branch);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogBranchCreated(logger, branch.Id, branch.Code);
        return branch.ToResponse();
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Branch {BranchId} created with code {BranchCode}")]
    private static partial void LogBranchCreated(ILogger logger, Guid branchId, string branchCode);
}
