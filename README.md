# App Service Builder: Redmond & Enns weather

A standalone, minimal ASP.NET app for learning how to deploy committed GitHub
source with [App Service Builder](https://builderapps.azure.com/docs/). The app
targets .NET 10 and serves a weather dashboard with a same-host Open-Meteo proxy.
The frontend is plain HTML, CSS, and JavaScript with no frontend build step.

**App Service Builder is in Private Preview. Use this sample for development and
testing, not production.** Local validation does not establish preview runtime
availability or end-to-end deployment readiness. A live preview deployment of
this sample has not been qualified.

## Features

| Endpoint | Response |
| --- | --- |
| `GET /` | HTTP 200, responsive weather page for Redmond, Washington, and Enns, Austria |
| `GET /api/weather/redmond` | Current conditions and a seven-day forecast for Redmond |
| `GET /api/weather/enns` | Current conditions and a seven-day forecast for Enns |
| `GET /health` | HTTP 200, `OK` followed by a newline |
| Unsupported city or unknown path | HTTP 404 |
| `GET /openapi/v1.json` | OpenAPI document, Development environment only |

Weather is displayed in English, Celsius and km/h, with city-local times and
forecast dates. The browser calls only this host's API; the server calls the
fixed HTTPS Open-Meteo forecast endpoint, using fixed coordinates and timezones.
No arbitrary upstream URL, city search, API key, or database is required.

Successful weather responses are cached in memory for five minutes per city.
Opening the page, **Refresh both**, and each city's **Refresh/Retry** button
request weather; refresh respects the server cache rather than forcing an
upstream request. Concurrent requests for a city share a successful fetch.
Each city loads independently. Failed refreshes clearly label any previous data;
the API never returns expired cached data as a successful fallback.

Upstream failures or invalid data return HTTP 502 Problem Details; upstream
timeouts return HTTP 504. Upstream requests time out after ten seconds. Missing
optional measurements display as unavailable rather than zero. Current
conditions are modelled estimates, not live station observations.

Data is provided by [Open-Meteo](https://open-meteo.com/) under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/); attribution is also
shown on the page. Check its current [terms](https://open-meteo.com/en/terms) and
[usage plans](https://open-meteo.com/en/pricing) before commercial or production
use; the free keyless API has usage restrictions and limits.

The old greeting and `SAMPLE_GREETING` setting are no longer used.
The app does not expose environment diagnostics. `builder.yaml` describes
one .NET 10 web component, listening on port 8080 with `/health` as its health
check. The manifest relies on Builder defaults for root routing, port 8080, and
HTTP 200 as the successful health-check status.

## Getting Started

### Prerequisites

- Git and a [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
  on Windows, macOS, or Linux. `global.json` requires SDK `10.0.100` or a later
  stable .NET 10 feature band; preview SDKs are not selected.
- For the optional smoke test: Bash and `curl`, with port 8080 available.
- For deployment: access to the App Service Builder Private Preview, an authorized
  Azure subscription/resource group, and GitHub source access in the Portal.
  Use a location available to your preview subscription; this sample does not
  promise availability in any particular region.

### Installation

Fork [this repository](https://github.com/Azure-Samples/app-service-builder-dotnet)
to your own GitHub account, then clone your fork. Replace `YOUR-GITHUB-USER`:

```sh
git clone https://github.com/YOUR-GITHUB-USER/app-service-builder-dotnet.git
cd app-service-builder-dotnet
dotnet --version
```

The application and `builder.yaml` are already at the repository root; no archive
extraction or monorepo subdirectory selection is needed. Keep `global.json`,
`Directory.Build.props`, and `NuGet.Config` with the project. They isolate SDK
selection, parent MSBuild properties, and public NuGet sources, respectively.
The explicit `--configfile NuGet.Config` below avoids inherited package feeds.

### Quickstart: run locally

From the repository root:

```sh
dotnet publish BuilderDotnetSample.csproj -c Release -o bin/sample-publish --configfile NuGet.Config
dotnet bin/sample-publish/BuilderDotnetSample.dll --contentRoot "$PWD/bin/sample-publish"
```

In another terminal:

```sh
curl -i http://localhost:8080/
curl -i http://localhost:8080/health
curl -i http://localhost:8080/api/weather/redmond
curl -i http://localhost:8080/api/weather/enns
curl -i http://localhost:8080/not-found
```

Open http://localhost:8080/ in a browser to see both cities. Live weather requires
outbound HTTPS access to `api.open-meteo.com`. Request examples are also available
in [weather.http](weather.http). Set `ASPNETCORE_ENVIRONMENT=Development` to expose
the OpenAPI document locally.

Expect the responses in the table above. `Program.cs` explicitly binds
`http://0.0.0.0:8080`; changing `PORT` or `ASPNETCORE_URLS` does not change that
binding. Stop the local process with Ctrl+C.

### Local validation

On macOS, Linux, or a Bash environment with the required tools:

```sh
dotnet build BuilderDotnetSample.csproj --configfile NuGet.Config
dotnet test tests/BuilderDotnetSample.Tests/BuilderDotnetSample.Tests.csproj -p:RestoreConfigFile=NuGet.Config
bash scripts/smoke-test.sh
```

The script publishes independently, starts and stops only its own processes, and
checks the published page, CSS/JavaScript, Development OpenAPI document, exact
health body, unsupported city, and unknown-path 404. It uses port 8080; stop
your earlier local run first. The xUnit tests use fake upstream HTTP responses
to cover both cities, response validation, timezone/DST dates, caching and expiry,
concurrency, failures, cancellation, and API error responses. Neither check
depends on live Open-Meteo availability.

The GitHub Actions workflow runs the smoke check and tests on pull requests and
changes to `main`. Generated `bin/` and `obj/` files are not committed.

## Demo

### Deploy your fork through the Portal

Follow the [Portal quickstart](https://builderapps.azure.com/docs/quickstarts/portal/)
for the currently available Private Preview experience.

1. Ensure your fork's `main` branch contains the sample files. Open the App Service
   Builder Portal and create an app in your authorized subscription and resource
   group, using a location available to your preview subscription.
2. Authorize GitHub source access through the Portal. Select **your fork**, its
   **main** branch, and the **repository root** as the source root. Keep
   `builder.yaml` at that root. Do not select the upstream repository if you plan
   to change and deploy your own code.
3. Explicitly start a deployment using the Portal's deploy action. Wait for a
   successful terminal deployment state; source authorization or saving a source
   selection alone does not prove that an app is running.
4. Open the runtime URL shown for your app. Verify `/` displays both cities and
   `/health` returns HTTP 200 with `OK`. If deployment fails, inspect its logs
   before retrying; do not treat an accepted request as successful deployment.

The manifest selects .NET platform version `"10"` and the standard publish path,
with no custom build command or output override. The start command is
`dotnet BuilderDotnetSample.dll`. Local publish validates that assembly name, not
the build image or live deployment pipeline.

### Change and redeploy

For a code change, edit your fork, commit, and push to the configured `main`
branch. Then explicitly deploy in the Portal, wait for successful completion,
and verify the root and health endpoints again. Pushing source by itself is not
a deployment.

To change the page, edit `wwwroot/index.html`, `wwwroot/styles.css`, or
`wwwroot/app.js`. Supported coordinates and upstream fields are defined in
`Weather/WeatherService.cs`. Publish again for local verification; for a deployed
app, commit and explicitly redeploy as described above. No weather credentials
or greeting variables are needed. Do not override the reserved `PORT` variable.

### Cleanup

Stop a local run with Ctrl+C. When finished with the preview, delete **only the
app you created** through the Portal, confirm its identity, and wait for deletion
to complete. Do not delete shared resource groups or unrelated resources. App
deletion does not remove your fork or separate resources.

## Resources

- [App Service Builder documentation](https://builderapps.azure.com/docs/)
- [Portal quickstart](https://builderapps.azure.com/docs/quickstarts/portal/)
- [Samples](https://builderapps.azure.com/docs/samples/)
- [Runtime requirements](https://builderapps.azure.com/docs/reference/runtimes/)
- [Variables guide](https://builderapps.azure.com/docs/guides/variables/)
- [Contributing](CONTRIBUTING.md) and [Microsoft Open Source Code of Conduct](.github/CODE_OF_CONDUCT.md)
- [MIT license](LICENSE.md)

For security concerns, follow the [Microsoft Security Response Center reporting
guidance](https://www.microsoft.com/msrc/faqs-report-an-issue) rather than opening
a public issue containing sensitive information.
