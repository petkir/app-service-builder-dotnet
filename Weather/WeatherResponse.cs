namespace BuilderDotnetSample.Weather;

/// <summary>Weather for a supported city, including seven local forecast dates.</summary>
public sealed record WeatherResponse(
    string City,
    string Name,
    string Country,
    string Timezone,
    DateTimeOffset FetchedAt,
    WeatherUnitsResponse Units,
    CurrentWeatherResponse Current,
    IReadOnlyList<DailyWeatherResponse> Daily);

/// <summary>Units used for all weather measurements.</summary>
public sealed record WeatherUnitsResponse(string Temperature, string WindSpeed, string PrecipitationProbability);

/// <summary>Current modelled conditions; unavailable optional measurements are null.</summary>
public sealed record CurrentWeatherResponse(
    DateTimeOffset ObservedAt,
    double Temperature,
    double? FeelsLike,
    double? Humidity,
    double? WindSpeed,
    int WeatherCode,
    bool IsDay);

/// <summary>Forecast for a city-local calendar date; rain probability may be unavailable.</summary>
public sealed record DailyWeatherResponse(
    DateOnly Date,
    int WeatherCode,
    double TemperatureMax,
    double TemperatureMin,
    double? PrecipitationProbability);
