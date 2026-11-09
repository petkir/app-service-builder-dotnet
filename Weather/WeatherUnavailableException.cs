namespace BuilderDotnetSample.Weather;

public sealed class WeatherUnavailableException(bool timedOut, Exception innerException)
    : Exception("The weather provider is unavailable.", innerException)
{
    public bool TimedOut { get; } = timedOut;
}
