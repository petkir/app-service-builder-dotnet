"use strict";

const locations = [
  { id: "redmond", name: "Redmond", region: "Washington, USA", number: "01" },
  { id: "enns", name: "Enns", region: "Upper Austria, Austria", number: "02" }
];
const panels = new Map();
const pending = new Set();
const refreshAll = document.querySelector("#refresh-all");
const number = new Intl.NumberFormat("en", { maximumFractionDigits: 0, signDisplay: "negative" });

function condition(code, isDay = true) {
  if (code === 0) return [isDay ? "Clear sky" : "Clear night", isDay ? "\u2600\ufe0f" : "\ud83c\udf19"];
  if (code === 1) return ["Mainly clear", "\ud83c\udf24\ufe0f"];
  if (code === 2) return ["Partly cloudy", "\u26c5"];
  if (code === 3) return ["Overcast", "\u2601\ufe0f"];
  if ([45, 48].includes(code)) return ["Fog", "\ud83c\udf2b\ufe0f"];
  if ([51, 53, 55].includes(code)) return ["Drizzle", "\ud83c\udf26\ufe0f"];
  if ([56, 57].includes(code)) return ["Freezing drizzle", "\ud83c\udf27\ufe0f"];
  if ([61, 63, 65].includes(code)) return ["Rain", "\ud83c\udf27\ufe0f"];
  if ([66, 67].includes(code)) return ["Freezing rain", "\ud83c\udf27\ufe0f"];
  if ([71, 73, 75, 77].includes(code)) return ["Snow", "\u2744\ufe0f"];
  if ([80, 81, 82].includes(code)) return ["Rain showers", "\ud83c\udf26\ufe0f"];
  if ([85, 86].includes(code)) return ["Snow showers", "\ud83c\udf28\ufe0f"];
  if (code === 95) return ["Thunderstorm", "\u26c8\ufe0f"];
  if ([96, 99].includes(code)) return ["Thunderstorm with hail", "\u26c8\ufe0f"];
  return ["Unknown conditions", "?"];
}

function measurement(value, unit) {
  return value === null ? "Unavailable" : `${number.format(value)}${unit}`;
}

function localTime(value, timezone) {
  return new Intl.DateTimeFormat("en", {
    month: "short", day: "numeric", hour: "numeric", minute: "2-digit",
    timeZone: timezone, timeZoneName: "short"
  }).format(new Date(value));
}

function element(tag, text, className) {
  const node = document.createElement(tag);
  node.textContent = text;
  if (className) node.className = className;
  return node;
}

function render(panel, data) {
  const current = data.current;
  const [description, icon] = condition(current.weatherCode, current.isDay);
  const temperature = panel.querySelector(".temperature");
  temperature.replaceChildren(
    document.createTextNode(number.format(current.temperature)),
    element("span", data.units.temperature, "unit"));
  panel.querySelector(".condition").textContent = description;
  panel.querySelector(".current-icon").textContent = icon;
  panel.querySelector(".observation").textContent = `As of ${localTime(current.observedAt, data.timezone)}`;
  panel.querySelector(".feels-like").textContent = measurement(current.feelsLike, data.units.temperature);
  panel.querySelector(".humidity").textContent = measurement(current.humidity, "%");
  panel.querySelector(".wind").textContent = measurement(current.windSpeed, ` ${data.units.windSpeed}`);
  panel.querySelector("caption").textContent = `${data.name} seven-day forecast. Rain column shows precipitation probability.`;

  const rows = data.daily.map(day => {
    // Date-only values are calendar labels, not instants in the browser's timezone.
    const date = new Date(`${day.date}T12:00:00Z`);
    const weekday = new Intl.DateTimeFormat("en", {
      weekday: "short", month: "short", day: "numeric", timeZone: "UTC"
    }).format(date);
    const row = document.createElement("tr");
    const dateCell = element("th", weekday);
    dateCell.scope = "row";
    dateCell.title = weekday;
    row.append(dateCell);
    const [label, symbol] = condition(day.weatherCode);
    const conditions = document.createElement("td");
    conditions.setAttribute("aria-label", label);
    const iconNode = element("span", symbol, "daily-icon");
    iconNode.setAttribute("aria-hidden", "true");
    const labelNode = element("span", label, "daily-label");
    labelNode.setAttribute("aria-hidden", "true");
    conditions.append(iconNode, labelNode);
    row.append(conditions,
      element("td", measurement(day.temperatureMax, "\u00b0")),
      element("td", measurement(day.temperatureMin, "\u00b0")),
      element("td", measurement(day.precipitationProbability, "%")));
    return row;
  });
  panel.querySelector("tbody").replaceChildren(...rows);
  panel.querySelector(".updated").textContent = `Fetched ${localTime(data.fetchedAt, data.timezone)}`;
  panel.querySelector(".weather-content").hidden = false;
}

async function loadCity(id) {
  if (pending.has(id)) return;
  const panel = panels.get(id);
  const status = panel.querySelector(".card-status");
  const error = panel.querySelector(".error");
  const button = panel.querySelector(".city-refresh");
  pending.add(id);
  refreshAll.disabled = true;
  button.disabled = true;
  panel.setAttribute("aria-busy", "true");
  status.textContent = `Loading ${panel.querySelector("h2").textContent} weather\u2026`;
  error.hidden = true;
  try {
    const response = await fetch(`/api/weather/${id}`, {
      headers: { Accept: "application/json" }, cache: "no-store", signal: AbortSignal.timeout(15000)
    });
    if (!response.ok) {
      throw new Error(response.status === 504
        ? "The weather provider timed out. Please retry."
        : "Weather is temporarily unavailable. Please retry.");
    }
    const data = await response.json();
    render(panel, data);
    panel.classList.remove("stale");
    status.textContent = "Current conditions & the next 7 days";
    button.textContent = "Refresh";
  } catch (exception) {
    console.error(`Weather request failed for ${id}`, exception);
    const previousData = !panel.querySelector(".weather-content").hidden;
    panel.classList.toggle("stale", previousData);
    status.textContent = previousData ? "Previous data \u2014 refresh failed" : "Weather could not be loaded";
    error.textContent = exception instanceof Error && exception.message.startsWith("The weather provider")
      ? exception.message : "Unable to load weather. Check your connection or try again shortly.";
    error.hidden = false;
    button.textContent = "Retry";
  } finally {
    pending.delete(id);
    button.disabled = false;
    panel.setAttribute("aria-busy", "false");
    refreshAll.disabled = pending.size > 0;
  }
}

for (const city of locations) {
  const panel = document.querySelector("#city-template").content.firstElementChild.cloneNode(true);
  panel.dataset.city = city.id;
  const title = panel.querySelector(".city-name");
  title.id = `city-${city.id}`;
  title.textContent = city.name;
  panel.setAttribute("aria-labelledby", title.id);
  panel.querySelector(".city-region").textContent = city.region;
  panel.querySelector(".city-number").textContent = city.number;
  const button = panel.querySelector(".city-refresh");
  button.setAttribute("aria-label", `Refresh weather for ${city.name}`);
  button.addEventListener("click", () => loadCity(city.id));
  panels.set(city.id, panel);
  document.querySelector("#cities").append(panel);
}

refreshAll.addEventListener("click", () => {
  for (const city of locations) void loadCity(city.id);
});
for (const city of locations) void loadCity(city.id);
