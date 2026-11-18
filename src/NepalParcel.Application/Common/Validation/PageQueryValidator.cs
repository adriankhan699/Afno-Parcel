using FluentValidation;
using NepalParcel.Contracts.Common;

namespace NepalParcel.Application.Common.Validation;

public sealed class PageQueryValidator : AbstractValidator<PageQuery>
{
    public PageQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PageQuery.MaxPageSize);
    }
}
