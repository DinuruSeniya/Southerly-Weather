// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Windows.Widgets.Providers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CsConsoleWidgetProvider
{
    internal class WeatherWidget : WidgetImplBase
    {
        public static string DefinitionId { get; } = "CSharp_Weather_Widget";

        private const string BomApiBase = "https://api.weather.bom.gov.au/v1";

        // State is encoded as "suburb|||pageNumber" so it survives restarts.
        // If no delimiter is present the whole string is treated as the suburb (legacy).
        private const string StateSep = "|||";

        private static readonly HttpClient HttpClient = new HttpClient();
        private static string WidgetTemplate { get; set; } = "";

        // Cache of condition key → base64 data URI so each image is read from
        // disk only once per process lifetime.
        private static readonly Dictionary<string, string> BgCache = new();

        // Fires every 30 min to match BOM's observation update cadence.
        private Timer? _refreshTimer;

        static WeatherWidget()
        {
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            HttpClient.DefaultRequestHeaders.Accept
                .Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        }

        public WeatherWidget(string widgetId, string initialState) : base(widgetId, initialState) { }

        // ── State helpers ─────────────────────────────────────────────────────

        private string Suburb
        {
            get  => state.Contains(StateSep) ? state.Split(StateSep)[0] : state;
            set  => state = EncodeState(value, Page);
        }

        private int Page
        {
            get  => state.Contains(StateSep) && int.TryParse(state.Split(StateSep)[1], out var p) ? p : 0;
            set  => state = EncodeState(Suburb, value);
        }

        private static string EncodeState(string suburb, int page) => $"{suburb}{StateSep}{page}";

        // ── Template ─────────────────────────────────────────────────────────

        private static string GetDefaultTemplate()
        {
            if (string.IsNullOrEmpty(WidgetTemplate))
                WidgetTemplate = ReadPackageFileFromUri("ms-appx:///Templates/WeatherWidgetTemplate.json");
            return WidgetTemplate;
        }

        public override string GetTemplateForWidget() => GetDefaultTemplate();

        // ── Data ─────────────────────────────────────────────────────────────

        public override string GetDataForWidget()
        {
            if (string.IsNullOrWhiteSpace(Suburb))
                return BuildEmptyPayload();

            try
            {
                return FetchBomWeatherAsync(Suburb, Page).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WeatherWidget] Error: {ex.Message}");
                return BuildErrorPayload(Suburb, Page, "Could not fetch BOM data. Check your connection and try again.");
            }
        }

        // ── Actions ───────────────────────────────────────────────────────────

        public override void OnActionInvoked(WidgetActionInvokedArgs args)
        {
            switch (args.Verb)
            {
                case "setLocation":
                    var inputs = JsonNode.Parse(args.Data);
                    var newSuburb = inputs?["suburb"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(newSuburb))
                    {
                        Suburb = newSuburb.Trim();
                        Page   = 0;
                        Console.WriteLine($"[WeatherWidget] Suburb set to: {Suburb}");
                        PushUpdate();
                    }
                    break;

                case "prevPage":
                    if (Page > 0) { Page--; PushUpdate(); }
                    break;

                case "nextPage":
                    if (Page < 2) { Page++; PushUpdate(); }
                    break;
            }
        }

        public override void Activate(WidgetContext widgetContext)
        {
            isActivated = true;
            if (!string.IsNullOrWhiteSpace(Suburb))
                PushUpdate();

            // Refresh every 30 minutes — BOM publishes new observations on the half-hour.
            _refreshTimer = new Timer(
                _ => { if (isActivated) PushUpdate(); },
                null,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(30));
        }

        public override void Deactivate()
        {
            isActivated = false;
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }

        // ── Push helper ───────────────────────────────────────────────────────

        private void PushUpdate()
        {
            var options = new WidgetUpdateRequestOptions(Id)
            {
                Template    = GetTemplateForWidget(),
                Data        = GetDataForWidget(),
                CustomState = State
            };
            WidgetManager.GetDefault().UpdateWidget(options);
        }

        // ── BOM fetch ─────────────────────────────────────────────────────────

        private static async Task<string> FetchBomWeatherAsync(string suburb, int page)
        {
            // 1. Resolve suburb → geohash (6-char)
            var searchUrl = $"{BomApiBase}/locations?search={Uri.EscapeDataString(suburb)}";
            var searchJson = JsonNode.Parse(await HttpClient.GetStringAsync(searchUrl));
            var locations  = searchJson?["data"]?.AsArray();

            if (locations == null || locations.Count == 0)
                return BuildErrorPayload(suburb, page, "Suburb not found. Try including the state, e.g. \"Fitzroy VIC\".");

            var rawHash = locations[0]!["geohash"]!.GetValue<string>();
            var geohash = rawHash.Length > 6 ? rawHash[..6] : rawHash;

            var locName   = locations[0]!["name"]!.GetValue<string>();
            var locState  = locations[0]?["state"]?.GetValue<string>() ?? "";
            var displaySuburb = string.IsNullOrEmpty(locState) ? locName : $"{locName}, {locState}";

            // 2. Current observations (always fetch — needed on all pages for header)
            var obsJson = JsonNode.Parse(
                await HttpClient.GetStringAsync($"{BomApiBase}/locations/{geohash}/observations"));
            var obs = obsJson?["data"];

            var temp         = obs?["temp"]?.GetValue<double?>();
            var feelsLike    = obs?["temp_feels_like"]?.GetValue<double?>();
            var humidity     = obs?["humidity"]?.GetValue<double?>();
            var rainSince9am = obs?["rain_since_9am"]?.GetValue<double?>();
            var windKmh      = obs?["wind"]?["speed_kilometre"]?.GetValue<double?>();
            var windDir      = obs?["wind"]?["direction"]?.GetValue<string>() ?? "";
            var stationName  = obs?["station"]?["name"]?.GetValue<string>() ?? "";
            var stationDistM = obs?["station"]?["distance"]?.GetValue<double?>();
            var stationDistKm = stationDistM.HasValue ? $"{stationDistM.Value / 1000.0:0.#} km" : "";

            // 3. Daily forecast (7 days, capped to 6)
            var dailyForecasts = new List<object>();
            var hourlyForecasts = new List<object>();
            var todayIconDesc  = "";   // captured from today's forecast for the current-conditions page

            try
            {
                var fcJson = JsonNode.Parse(
                    await HttpClient.GetStringAsync($"{BomApiBase}/locations/{geohash}/forecasts/daily"));
                var days = fcJson?["data"]?.AsArray();
                if (days != null)
                {
                    var today = DateTime.Today;
                    var first = true;
                    foreach (var d in days.Take(6))
                    {
                        var dateStr  = d?["date"]?.GetValue<string>() ?? "";
                        var dayLabel = "Today";
                        if (DateTimeOffset.TryParse(dateStr, out var dt))
                        {
                            var local = dt.LocalDateTime.Date;
                            dayLabel = local == today            ? "Today"
                                     : local == today.AddDays(1) ? "Tomorrow"
                                     : local.ToString("ddd d MMM");
                        }

                        var iconDesc = d?["icon_descriptor"]?.GetValue<string>() ?? "";
                        if (first) { todayIconDesc = iconDesc; first = false; }

                        dailyForecasts.Add(new
                        {
                            dayLabel,
                            iconUrl     = GetIconUrl(iconDesc),
                            desc        = d?["short_text"]?.GetValue<string>() ?? "",
                            maxTemp     = FormatTemp(d?["temp_max"]?.GetValue<double?>()),
                            minTemp     = FormatTemp(d?["temp_min"]?.GetValue<double?>()),
                            rainChance  = d?["rain"]?["chance"]?.GetValue<int?>() is int rc ? $"{rc}%" : ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WeatherWidget] Daily forecast error: {ex.Message}");
            }

            // 4. Hourly forecast (next 12 hours)
            try
            {
                var hrJson = JsonNode.Parse(
                    await HttpClient.GetStringAsync($"{BomApiBase}/locations/{geohash}/forecasts/hourly"));
                var hours = hrJson?["data"]?.AsArray();
                if (hours != null)
                {
                    foreach (var h in hours.Take(12))
                    {
                        var timeStr  = h?["time"]?.GetValue<string>() ?? "";
                        var timeLabel = DateTimeOffset.TryParse(timeStr, out var dt)
                            ? dt.LocalDateTime.ToString("h tt")   // e.g. "2 PM"
                            : "";

                        var iconDesc = h?["icon_descriptor"]?.GetValue<string>() ?? "";
                        var hWind    = h?["wind"]?["speed_kilometre"]?.GetValue<double?>();
                        var hWindDir = h?["wind"]?["direction"]?.GetValue<string>() ?? "";

                        hourlyForecasts.Add(new
                        {
                            timeLabel,
                            iconUrl    = GetIconUrl(iconDesc),
                            temp       = FormatTemp(h?["temp"]?.GetValue<double?>()),
                            rainChance = h?["rain"]?["chance"]?.GetValue<int?>() is int rc ? $"{rc}%" : "",
                            wind       = hWind.HasValue ? $"{(int)hWind.Value} {hWindDir}".Trim() : ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WeatherWidget] Hourly forecast error: {ex.Message}");
            }

            // 5. Active weather warnings for this location
            var warnings = new List<object>();
            try
            {
                var warnJson = JsonNode.Parse(
                    await HttpClient.GetStringAsync($"{BomApiBase}/locations/{geohash}/warnings"));
                var warnData = warnJson?["data"]?.AsArray();
                if (warnData != null)
                {
                    foreach (var w in warnData)
                    {
                        var title = w?["title"]?.GetValue<string>() ?? "";
                        if (!string.IsNullOrWhiteSpace(title))
                            warnings.Add(new { title });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WeatherWidget] Warnings fetch error: {ex.Message}");
            }

            // Resolve background: read from package, encode as data URI so the
            // Widget Host (a different process) doesn't need to resolve ms-appx://.
            var backgroundDataUri = await GetBackgroundDataUriAsync(todayIconDesc);

            return JsonSerializer.Serialize(new
            {
                hasData           = true,
                page,
                canGoPrev         = page > 0,
                canGoNext         = page < 2,
                suburb            = displaySuburb,
                iconUrl           = GetIconUrl(todayIconDesc),
                backgroundImageUrl = backgroundDataUri,
                headerIconUrl     = GetHeaderIconUrl(todayIconDesc),
                // Current (page 0)
                temperature       = FormatTemp(temp),
                feelsLike         = FormatTemp(feelsLike),
                humidity          = humidity.HasValue ? $"{(int)humidity.Value}%" : "",
                rain              = rainSince9am.HasValue ? $"{rainSince9am.Value:0.#} mm" : "",
                wind              = windKmh.HasValue ? $"{(int)windKmh.Value} km/h {windDir}".Trim() : "",
                stationName,
                stationDist       = stationDistKm,
                // Forecast pages
                dailyForecasts,
                hourlyForecasts,
                warnings,
                hasWarnings       = warnings.Count > 0,
                error             = ""
            });
        }

        // ── Static helpers ────────────────────────────────────────────────────

        private static string FormatTemp(double? val)
            => val.HasValue ? $"{val.Value:0.#}" : "";

        private static string BuildEmptyPayload() => JsonSerializer.Serialize(new
        {
            hasData = false, page = 0, canGoPrev = false, canGoNext = false,
            suburb = "", iconUrl = "", backgroundImageUrl = "", headerIconUrl = "",
            temperature = "", feelsLike = "", humidity = "",
            rain = "", wind = "", stationName = "", stationDist = "",
            dailyForecasts = Array.Empty<object>(),
            hourlyForecasts = Array.Empty<object>(),
            warnings = Array.Empty<object>(), hasWarnings = false,
            error = ""
        });

        private static string BuildErrorPayload(string suburb, int page, string error) =>
            JsonSerializer.Serialize(new
            {
                hasData = true, page, canGoPrev = page > 0, canGoNext = page < 2,
                suburb, iconUrl = "", backgroundImageUrl = "", headerIconUrl = "",
                temperature = "", feelsLike = "", humidity = "",
                rain = "", wind = "", stationName = "", stationDist = "",
                dailyForecasts = Array.Empty<object>(),
                hourlyForecasts = Array.Empty<object>(),
                warnings = Array.Empty<object>(), hasWarnings = false,
                error
            });

        // ── Image URL helpers ─────────────────────────────────────────────────
        //
        // Icons  : OpenWeatherMap PNG icons — free, no API key, reliable HTTPS.
        //          Used for Image elements in the card body and forecast rows.
        //
        // Backgrounds: Bundled package assets via ms-appx://.
        //   Add these JPG files to your project under Assets/WeatherBackgrounds/
        //   and mark them as Content in the .csproj (CopyToOutputDirectory = Always):
        //     sunny.jpg, partly_cloudy.jpg, cloudy.jpg, rain.jpg,
        //     storm.jpg, snow.jpg, fog.jpg, night.jpg
        //   Free sources: Unsplash (unsplash.com), Pexels (pexels.com), or
        //   the Australian BOM photo library (bom.gov.au/climate/photos/).
        //   If a file is missing the background simply won't render — the
        //   widget still works fine (graceful degradation).
        //
        // Header icon: Also uses OWM PNG (HTTPS required; ms-appx:// is for
        //   resources resolved inside the provider process, not the Widget Host).

        private static string GetIconUrl(string descriptor)
        {
            // Map BOM icon_descriptor → OpenWeatherMap icon code
            var code = descriptor switch
            {
                "sunny"                         => "01d",
                "clear"                         => "01n",
                "mostly_sunny"                  => "02d",
                "partly_cloudy"                 => "02d",
                "mostly_cloudy"                 => "03d",
                "cloudy" or "overcast"          => "04d",
                "hazy" or "fog"                 => "50d",
                "frost"                         => "13d",
                "light_shower" or "light_rain"  => "10d",
                "shower" or "rain"              => "10d",
                "heavy_shower" or "heavy_rain"  => "09d",
                "storm"                         => "11d",
                "snow"                          => "13d",
                "wind"                          => "50d",
                _                               => "03d"
            };
            return $"https://openweathermap.org/img/wn/{code}@2x.png";
        }

        private static string GetBgKey(string descriptor) => descriptor switch
        {
            "sunny" or "mostly_sunny"                              => "sunny",
            "partly_cloudy" or "mostly_cloudy"                    => "partly_cloudy",
            "cloudy" or "overcast" or "hazy"                      => "cloudy",
            "light_shower" or "shower" or "rain"
                or "light_rain" or "heavy_shower" or "heavy_rain" => "rain",
            "storm"                                                => "storm",
            "snow" or "frost"                                      => "snow",
            "fog"                                                  => "fog",
            "clear"                                                => "night",
            _                                                      => "cloudy"
        };

        // Reads the bundled background JPEG from the provider's own package
        // using plain System.IO — no WinRT dependency needed.
        // In an MSIX packaged app, AppContext.BaseDirectory is the package
        // install root, so the path resolves correctly alongside the executable.
        // Results are cached — each image is only read from disk once per run.
        private static async Task<string> GetBackgroundDataUriAsync(string descriptor)
        {
            var key = GetBgKey(descriptor);

            lock (BgCache)
                if (BgCache.TryGetValue(key, out var cached))
                    return cached;

            try
            {
                var filePath = Path.Combine(
                    AppContext.BaseDirectory,
                    "Assets", "WeatherBackgrounds", $"{key}.jpg");

                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"[WeatherWidget] Background not found: {filePath}");
                    lock (BgCache) BgCache[key] = "";
                    return "";
                }

                var bytes   = await File.ReadAllBytesAsync(filePath);
                var dataUri = $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";

                lock (BgCache) BgCache[key] = dataUri;
                Console.WriteLine($"[WeatherWidget] Background cached: {key} ({bytes.Length / 1024} KB)");
                return dataUri;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WeatherWidget] Background load failed for '{key}': {ex.Message}");
                lock (BgCache) BgCache[key] = "";
                return "";
            }
        }

        // Used in the Adaptive Card 'header' property — must be an HTTPS URL
        // because it is resolved by the Widget Host, not the provider process.
        private static string GetHeaderIconUrl(string descriptor) => GetIconUrl(descriptor);
    }
}
