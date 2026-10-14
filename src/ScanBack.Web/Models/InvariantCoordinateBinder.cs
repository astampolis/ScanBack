using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ScanBack.Web.Models;

/// <summary>Browser geolocation uses a decimal point regardless of the interface language.</summary>
public sealed class InvariantCoordinateBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext context)
    {
        var result = context.ValueProvider.GetValue(context.ModelName);
        if (result == ValueProviderResult.None || string.IsNullOrWhiteSpace(result.FirstValue))
        {
            context.Result = ModelBindingResult.Success(null);
            return Task.CompletedTask;
        }
        context.ModelState.SetModelValue(context.ModelName, result);
        if (double.TryParse(result.FirstValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            context.Result = ModelBindingResult.Success(value);
        else
            context.ModelState.TryAddModelError(context.ModelName, "The location coordinate is invalid.");
        return Task.CompletedTask;
    }
}
