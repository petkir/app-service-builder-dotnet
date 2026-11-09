using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuilderDotnetSample.Weather;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuilderDotnetSample.Tests;

public sealed class WeatherTests
{
    [Theory]
    [InlineData("redmond", "America/Los_Angeles", "47.674", "-122.1215", -7)]
    [InlineData("enns", "Europe/Vienna", "48.21346", "14.47612", 1)]
    public async Task SupportedCityReturnsSevenLocalDaysAndExplicitUnits(
        string city, string timezone, string latitude, string longitude, int offset)
    {
        using var upstream = new FakeUpstream(request =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("api.open-meteo.com", request.RequestUri.Host);
            Assert.Equal("/v1/forecast", request.RequestUri.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal(latitude, query["latitude"].ToString());
            Assert.Equal(longitude, query["longitude"].ToString());
            Assert.Equal(timezone, query["timezone"].ToString());
            Assert.Equal("celsius", query["temperature_unit"].ToString());
            Assert.Equal("kmh", query["wind_speed_unit"].ToString());
            Assert.Equal("7", query["forecast_days"].ToString());
            Assert.Equal("unixtime", query["timeformat"].ToString());
            Assert.Equal("temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day",
                query["current"].ToString());
            Assert.Equal("weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max",
                query["daily"].ToString());
            return JsonResponse(Forecast(timezone));
        });
        using var factory = new WeatherFactory(upstream);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/weather/{city}");
        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var weather = await response.Content.ReadFromJsonAsync<WeatherResponse>();
        Assert.NotNull(weather);
        Assert.Equal(city, weather.City);
        Assert.Equal(timezone, weather.Timezone);
        Assert.Equal(new WeatherUnitsResponse("°C", "km/h", "%"), weather.Units);
        Assert.Equal(0, weather.Current.Temperature);
        Assert.Equal(TimeSpan.FromHours(offset), weather.Current.ObservedAt.Offset);
        Assert.Equal(7, weather.Daily.Count);
        Assert.Equal(Enumerable.Range(0, 7).Select(day => new DateOnly(2026, 11, 1).AddDays(day)),
            weather.Daily.Select(day => day.Date));
        Assert.Equal(city == "redmond" ? 25 : 24,
            (ForecastTimes(timezone)[1] - ForecastTimes(timezone)[0]) / 3600d);
    }

    [Fact]
    public async Task UnknownCityReturnsProblemWithoutUpstreamCall()
    {
        using var upstream = new FakeUpstream(_ => throw new InvalidOperationException("Must not call upstream."));
        using var factory = new WeatherFactory(upstream);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/weather/london");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(404, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Status);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task CacheExpiresAtFiveMinutesAndCoalescesConcurrentRequests()
    {
        var clock = new TestClock();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var upstream = new FakeUpstream(async (request, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return JsonResponse(Forecast(QueryHelpers.ParseQuery(request.RequestUri!.Query)["timezone"].ToString()));
        });
        using var service = new WeatherService(upstream, clock);
        var requests = Enumerable.Range(0, 8).Select(_ => service.GetAsync("redmond", default)).ToArray();
        await started.Task;
        release.SetResult();
        var responses = await Task.WhenAll(requests);
        Assert.Equal(1, upstream.Calls);
        Assert.All(responses, response => Assert.Same(responses[0], response));
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        Assert.Same(responses[0], await service.GetAsync("redmond", default));
        Assert.Equal(1, upstream.Calls);
        clock.Advance(TimeSpan.FromTicks(1));
        var refreshed = await service.GetAsync("redmond", default);
        Assert.NotSame(responses[0], refreshed);
        Assert.Equal(clock.GetUtcNow(), refreshed!.FetchedAt);
        Assert.Equal(2, upstream.Calls);
        await service.GetAsync("enns", default);
        Assert.Equal(3, upstream.Calls);
    }

    [Theory]
    [InlineData("http", 502)]
    [InlineData("transport", 502)]
    [InlineData("json", 502)]
    [InlineData("empty", 502)]
    [InlineData("timeout", 504)]
    public async Task UpstreamFailuresReturnSafeProblemDetailsAndAreNotCached(string failure, int status)
    {
        using var upstream = new FakeUpstream(_ => failure switch
        {
            "http" => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            "transport" => throw new HttpRequestException("private upstream details"),
            "json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{broken") },
            "empty" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null") },
            _ => throw new TaskCanceledException("private upstream details")
        });
        using var factory = new WeatherFactory(upstream);
        using var client = factory.CreateClient();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.GetAsync("/api/weather/redmond");
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private upstream", body);
            Assert.Contains("Please try again", body);
        }
        Assert.Equal(2, upstream.Calls);
    }

    [Theory]
    [InlineData("missing-current")]
    [InlineData("missing-temperature")]
    [InlineData("short-daily")]
    [InlineData("null-daily-code")]
    [InlineData("bad-date")]
    [InlineData("bad-units")]
    [InlineData("bad-timezone")]
    [InlineData("bad-timestamp")]
    [InlineData("bad-probability")]
    [InlineData("bad-high-low")]
    public async Task InvalidForecastIsRejected(string mutation)
    {
        var payload = Forecast("America/Los_Angeles");
        switch (mutation)
        {
            case "missing-current": payload["current"] = null; break;
            case "missing-temperature": payload["current"]!["temperature_2m"] = null; break;
            case "short-daily": payload["daily"]!["time"]!.AsArray().RemoveAt(6); break;
            case "null-daily-code": payload["daily"]!["weather_code"]![0] = null; break;
            case "bad-date": payload["daily"]!["time"]![0] = payload["daily"]!["time"]![1]!.DeepClone(); break;
            case "bad-units": payload["current_units"]!["temperature_2m"] = "°F"; break;
            case "bad-timezone": payload["timezone"] = "UTC"; break;
            case "bad-timestamp": payload["current"]!["time"] = long.MaxValue; break;
            case "bad-probability": payload["daily"]!["precipitation_probability_max"]![0] = 101; break;
            case "bad-high-low": payload["daily"]!["temperature_2m_max"]![0] = -10; break;
        }
        using var upstream = new FakeUpstream(_ => JsonResponse(payload));
        using var service = new WeatherService(upstream, new TestClock());
        var error = await Assert.ThrowsAsync<WeatherUnavailableException>(() => service.GetAsync("redmond", default));
        Assert.False(error.TimedOut);
    }

    [Fact]
    public async Task OptionalNullMeasurementsAreNotConvertedToZero()
    {
        var payload = Forecast("Europe/Vienna");
        payload["current"]!["apparent_temperature"] = null;
        payload["current"]!["relative_humidity_2m"] = null;
        payload["current"]!["wind_speed_10m"] = null;
        payload["daily"]!["precipitation_probability_max"]![0] = null;
        using var upstream = new FakeUpstream(_ => JsonResponse(payload));
        using var service = new WeatherService(upstream, new TestClock());
        var response = await service.GetAsync("enns", default);
        Assert.NotNull(response);
        Assert.Null(response.Current.FeelsLike);
        Assert.Null(response.Current.Humidity);
        Assert.Null(response.Current.WindSpeed);
        Assert.Null(response.Daily[0].PrecipitationProbability);
        Assert.Equal(0, response.Current.Temperature);
    }

    [Fact]
    public async Task ClientCancellationIsPropagatedAndDoesNotPoisonCache()
    {
        using var cts = new CancellationTokenSource();
        var observedCancellation = false;
        using var upstream = new FakeUpstream((_, ct) =>
        {
            cts.Cancel();
            observedCancellation = ct.IsCancellationRequested;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(JsonResponse(Forecast("Europe/Vienna")));
        });
        using var service = new WeatherService(upstream, new TestClock());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync("enns", cts.Token));
        Assert.True(observedCancellation);
        Assert.NotNull(await service.GetAsync("enns", default));
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task FailedRefreshDoesNotReturnExpiredCache()
    {
        var clock = new TestClock();
        var fail = false;
        using var upstream = new FakeUpstream(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : JsonResponse(Forecast("Europe/Vienna")));
        using var service = new WeatherService(upstream, clock);
        await service.GetAsync("enns", default);
        clock.Advance(TimeSpan.FromMinutes(5));
        fail = true;
        await Assert.ThrowsAsync<WeatherUnavailableException>(() => service.GetAsync("enns", default));
        fail = false;
        Assert.Equal(clock.GetUtcNow(), (await service.GetAsync("enns", default))!.FetchedAt);
        Assert.Equal(3, upstream.Calls);
    }

    [Fact]
    public async Task HealthStaticAssetsAndOpenApiAreAvailableAndUnknownPathsStay404()
    {
        using var upstream = new FakeUpstream(_ => throw new InvalidOperationException("Unexpected upstream call."));
        using var factory = new WeatherFactory(upstream);
        using var client = factory.CreateClient();
        Assert.Equal("OK\n", await client.GetStringAsync("/health"));
        Assert.Contains("Two cities.", await client.GetStringAsync("/"));
        Assert.Contains("/api/weather/", await client.GetStringAsync("/app.js"));
        Assert.Contains(".city-grid", await client.GetStringAsync("/styles.css"));
        using var unknown = await client.GetAsync("/not-found");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var api = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!;
        Assert.NotNull(api["paths"]!["/api/weather/{city}"]);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task ProductionDoesNotExposeOpenApi()
    {
        using var upstream = new FakeUpstream(_ => throw new InvalidOperationException("Unexpected upstream call."));
        using var factory = new WeatherFactory(upstream, "Production");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static long[] ForecastTimes(string timezone)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        return Enumerable.Range(0, 7).Select(day =>
        {
            var local = new DateTime(2026, 11, 1).AddDays(day);
            return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeSeconds();
        }).ToArray();
    }

    private static JsonObject Forecast(string timezone)
    {
        var times = ForecastTimes(timezone);
        return JsonSerializer.SerializeToNode(new
        {
            timezone,
            current_units = new { time = "unixtime", temperature_2m = "°C", wind_speed_10m = "km/h" },
            daily_units = new
            {
                time = "unixtime", temperature_2m_max = "°C", temperature_2m_min = "°C",
                precipitation_probability_max = "%"
            },
            current = new
            {
                time = times[0] + 1800, temperature_2m = 0, apparent_temperature = -1,
                relative_humidity_2m = 70, wind_speed_10m = 5, weather_code = 3, is_day = 0
            },
            daily = new
            {
                time = times, weather_code = Enumerable.Repeat(3, 7).ToArray(),
                temperature_2m_max = Enumerable.Repeat(10, 7).ToArray(),
                temperature_2m_min = Enumerable.Repeat(0, 7).ToArray(),
                precipitation_probability_max = Enumerable.Repeat(20, 7).ToArray()
            }
        })!.AsObject();
    }

    private static HttpResponseMessage JsonResponse(JsonObject payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
    };

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class FakeUpstream : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
        private int calls;
        public int Calls => calls;

        public FakeUpstream(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request))) { }

        public FakeUpstream(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            this.respond = respond;
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.open-meteo.com/")
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return respond(request, ct);
        }
    }

    private sealed class WeatherFactory(FakeUpstream upstream, string environment = "Development")
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(upstream);
            });
        }
    }
}
