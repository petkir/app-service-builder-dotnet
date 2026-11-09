using Microsoft.AspNetCore.Http.HttpResults;

namespace BuilderDotnetSample.Weather;

public static class WeatherEndpoints
{
    public static void MapWeather(this WebApplication app)
    {
        app.MapGet("/api/weather/{city}", async Task<Results<Ok<WeatherResponse>, ProblemHttpResult>> (
            string city, IWeatherService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var weather = await service.GetAsync(city, cancellationToken);
            return weather is null
                ? TypedResults.Problem(statusCode: 404, title: "Unknown city",
                    detail: "Supported cities are redmond and enns.")
                : TypedResults.Ok(weather);
        })
        .WithName("GetCityWeather")
        .WithSummary("Get current weather and a seven-day forecast")
        .WithDescription("Supports redmond and enns. Successful upstream responses are cached for five minutes.")
        .ProducesProblem(404)
        .ProducesProblem(502)
        .ProducesProblem(504);
    }
}
