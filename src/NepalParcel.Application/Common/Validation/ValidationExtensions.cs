using FluentValidation;
using NepalParcel.Application.Common.Exceptions;

namespace NepalParcel.Application.Common.Validation;

internal static class ValidationExtensions
{
    /// <summary>Validates and throws <see cref="RequestValidationException"/> with grouped errors.</summary>
    public static async Task ValidateOrThrowAsync<T>(
        this IValidator<T> validator,
        T instance,
        CancellationToken cancellationToken)
    {
        var result = await validator.ValidateAsync(instance, cancellationToken).ConfigureAwait(false);
        if (result.IsValid)
        {
            return;
        }

        var errors = result.Errors
            .GroupBy(e => e.PropertyName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray(), StringComparer.Ordinal);

        throw new RequestValidationException(errors);
    }
}
