using BuilderDotnetSample.Weather;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BuilderDotnetSample.Middleware;

public sealed class WeatherExceptionHandler(
    ILogger<WeatherExceptionHandler> logger,
    IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not WeatherUnavailableException weatherException)
        {
            return false;
        }

        logger.LogWarning(exception, "Weather provider failed for {Path}", context.Request.Path);
        context.Response.StatusCode = weatherException.TimedOut ? 504 : 502;
        context.Response.Headers.CacheControl = "no-store";
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = context.Response.StatusCode,
                Title = weatherException.TimedOut ? "Weather provider timed out" : "Weather temporarily unavailable",
                Detail = "Unable to load weather. Please try again.",
                Instance = context.Request.Path
            }
        });
        return true;
    }
}
