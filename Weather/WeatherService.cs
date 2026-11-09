using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuilderDotnetSample.Weather;

public sealed class WeatherService(IHttpClientFactory clients, TimeProvider clock) : IWeatherService, IDisposable
{
    private readonly Dictionary<string, CityState> cities = new(StringComparer.Ordinal)
    {
        ["redmond"] = new("Redmond", "Washington, USA", 47.6740, -122.1215, "America/Los_Angeles"),
        ["enns"] = new("Enns", "Austria", 48.21346, 14.47612, "Europe/Vienna")
    };

    public async Task<WeatherResponse?> GetAsync(string city, CancellationToken cancellationToken)
    {
        if (!cities.TryGetValue(city, out var state))
        {
            return null;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Cached is { } cached && clock.GetUtcNow() < cached.FetchedAt.AddMinutes(5))
            {
                return cached;
            }

            var query = FormattableString.Invariant(
                $"v1/forecast?latitude={state.Latitude}&longitude={state.Longitude}")
                + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day"
                + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
                + "&temperature_unit=celsius&wind_speed_unit=kmh&forecast_days=7&timeformat=unixtime"
                + $"&timezone={Uri.EscapeDataString(state.Timezone)}";
            using var client = clients.CreateClient("OpenMeteo");
            try
            {
                var payload = await client.GetFromJsonAsync<ForecastPayload>(query, cancellationToken)
                    ?? throw new InvalidDataException("Empty forecast.");
                var weather = Map(city, state, payload);
                state.Cached = weather;
                return weather;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WeatherUnavailableException(true, exception);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or InvalidDataException or ArgumentOutOfRangeException)
            {
                throw new WeatherUnavailableException(false, exception);
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private WeatherResponse Map(string city, CityState state, ForecastPayload payload)
    {
        var current = payload.Current ?? throw new InvalidDataException("Missing current conditions.");
        var daily = payload.Daily ?? throw new InvalidDataException("Missing daily forecast.");
        if (payload.Timezone != state.Timezone ||
            payload.CurrentUnits?.Temperature != "°C" || payload.CurrentUnits.WindSpeed != "km/h" ||
            payload.CurrentUnits.Time != "unixtime" ||
            payload.DailyUnits?.TemperatureMax != "°C" || payload.DailyUnits.TemperatureMin != "°C" ||
            payload.DailyUnits.PrecipitationProbability != "%" || payload.DailyUnits.Time != "unixtime" ||
            current.Time is null || current.Time <= 0 ||
            current.WeatherCode is null || current.IsDay is not (0 or 1) ||
            daily.Time?.Length != 7 || daily.WeatherCode?.Length != 7 ||
            daily.TemperatureMax?.Length != 7 || daily.TemperatureMin?.Length != 7 ||
            daily.PrecipitationProbability?.Length != 7)
        {
            throw new InvalidDataException("Incomplete forecast or unexpected units.");
        }

        var timezone = TimeZoneInfo.FindSystemTimeZoneById(state.Timezone);
        var observedAt = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(current.Time.Value), timezone);
        var firstDate = DateOnly.FromDateTime(observedAt.DateTime);
        var forecasts = new DailyWeatherResponse[7];
        for (var index = 0; index < forecasts.Length; index++)
        {
            var timestamp = daily.Time[index] ?? throw new InvalidDataException("Missing forecast date.");
            var date = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(timestamp), timezone).DateTime);
            var high = RequiredMeasurement(daily.TemperatureMax[index]);
            var low = RequiredMeasurement(daily.TemperatureMin[index]);
            if (date != firstDate.AddDays(index) || high < low || daily.WeatherCode[index] is null)
            {
                throw new InvalidDataException("Invalid daily forecast.");
            }

            forecasts[index] = new(date, daily.WeatherCode[index]!.Value, high, low,
                OptionalMeasurement(daily.PrecipitationProbability[index], 0, 100));
        }

        return new(city, state.Name, state.Country, state.Timezone, clock.GetUtcNow(),
            new("°C", "km/h", "%"),
            new(observedAt, RequiredMeasurement(current.Temperature),
                OptionalMeasurement(current.FeelsLike),
                OptionalMeasurement(current.Humidity, 0, 100),
                OptionalMeasurement(current.WindSpeed, 0),
                current.WeatherCode.Value, current.IsDay == 1),
            Array.AsReadOnly(forecasts));
    }

    private static double RequiredMeasurement(double? value) =>
        OptionalMeasurement(value) ?? throw new InvalidDataException("Missing required measurement.");

    private static double? OptionalMeasurement(
        double? value, double minimum = double.MinValue, double maximum = double.MaxValue)
    {
        if (value is { } measurement && (!double.IsFinite(measurement) || measurement < minimum || measurement > maximum))
        {
            throw new InvalidDataException("Invalid measurement.");
        }
        return value;
    }

    public void Dispose()
    {
        foreach (var state in cities.Values)
        {
            state.Gate.Dispose();
        }
    }

    private sealed class CityState(string name, string country, double latitude, double longitude, string timezone)
    {
        public string Name { get; } = name;
        public string Country { get; } = country;
        public double Latitude { get; } = latitude;
        public double Longitude { get; } = longitude;
        public string Timezone { get; } = timezone;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public WeatherResponse? Cached { get; set; }
    }

    internal sealed record ForecastPayload(
        string? Timezone,
        CurrentPayload? Current,
        DailyPayload? Daily,
        [property: JsonPropertyName("current_units")] CurrentUnits? CurrentUnits,
        [property: JsonPropertyName("daily_units")] DailyUnits? DailyUnits);

    internal sealed record CurrentPayload(
        long? Time,
        [property: JsonPropertyName("temperature_2m")] double? Temperature,
        [property: JsonPropertyName("apparent_temperature")] double? FeelsLike,
        [property: JsonPropertyName("relative_humidity_2m")] double? Humidity,
        [property: JsonPropertyName("wind_speed_10m")] double? WindSpeed,
        [property: JsonPropertyName("weather_code")] int? WeatherCode,
        [property: JsonPropertyName("is_day")] int? IsDay);

    internal sealed record DailyPayload(
        long?[]? Time,
        [property: JsonPropertyName("weather_code")] int?[]? WeatherCode,
        [property: JsonPropertyName("temperature_2m_max")] double?[]? TemperatureMax,
        [property: JsonPropertyName("temperature_2m_min")] double?[]? TemperatureMin,
        [property: JsonPropertyName("precipitation_probability_max")] double?[]? PrecipitationProbability);

    internal sealed record CurrentUnits(
        string? Time,
        [property: JsonPropertyName("temperature_2m")] string? Temperature,
        [property: JsonPropertyName("wind_speed_10m")] string? WindSpeed);

    internal sealed record DailyUnits(
        string? Time,
        [property: JsonPropertyName("temperature_2m_max")] string? TemperatureMax,
        [property: JsonPropertyName("temperature_2m_min")] string? TemperatureMin,
        [property: JsonPropertyName("precipitation_probability_max")] string? PrecipitationProbability);
}
