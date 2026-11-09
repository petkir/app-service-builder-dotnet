using BuilderDotnetSample.Middleware;
using BuilderDotnetSample.Weather;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<WeatherExceptionHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("OpenMeteo", client =>
{
    client.BaseAddress = new Uri("https://api.open-meteo.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<IWeatherService, WeatherService>();
var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Text("OK\n"));
app.MapWeather();

app.Run();

public partial class Program;
