namespace BuilderDotnetSample.Weather;

public interface IWeatherService
{
    Task<WeatherResponse?> GetAsync(string city, CancellationToken cancellationToken);
}
