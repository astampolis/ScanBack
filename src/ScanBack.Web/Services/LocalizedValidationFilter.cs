using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ScanBack.Web.Services;

/// <summary>Localizes known application messages without changing user input or exception details.</summary>
public sealed class LocalizedValidationFilter(UiText text) : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (!text.IsGreek || context.Result is not ViewResult) return;
        foreach (var entry in context.ModelState.Values)
        {
            var errors = entry.Errors.ToArray();
            if (!errors.Any(error => error.Exception is null
                && text[error.ErrorMessage] != error.ErrorMessage)) continue;

            entry.Errors.Clear();
            foreach (var error in errors)
                entry.Errors.Add(error.Exception is null && text[error.ErrorMessage] != error.ErrorMessage
                    ? new ModelError(text[error.ErrorMessage])
                    : error);
        }
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
