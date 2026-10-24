// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Windows.Widgets.Providers;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Southerly
{
    internal class WeatherWidget : WidgetImplBase
    {
        public static string DefinitionId { get; } = "Southerly_Weather";

        private const string BomApiBase = "https://api.weather.bom.gov.au/v1";

        // State is encoded as "suburb|||pageNumber|||geohash" so it survives restarts.
        // Older states may be just "suburb" or "suburb|||page"; the geohash is then
        // resolved from the suburb name on the next refresh.
        private const string StateSep = "|||";

        private static readonly HashSet<string> AuStates = new(StringComparer.OrdinalIgnoreCase)
        {
            "VIC", "NSW", "QLD", "SA", "WA", "TAS", "NT", "ACT"
        };

        // Most matches offered when a search is ambiguous.
        private const int MaxMatches = 6;

        // Fetched weather is reused for this long, so switching tabs is instant.
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

        // Background images are downscaled to this width before being sent as a data URI.
        private const int BgMaxWidth = 800;

        private static readonly HttpClient HttpClient = new HttpClient();
        private static string WidgetTemplate { get; set; } = "";

        // Cache of "condition key|theme" → base64 data URI so each image is processed
        // only once per theme per process lifetime.
        private static readonly Dictionary<string, string> BgCache = new();

        // Fires every 30 min to match BOM's observation update cadence.
        private Timer? _refreshTimer;

        // True while the location input is shown in place of the weather.
        private bool _editing;

        // Problem with the last location search (not found, etc.), shown under the input.
        private string _editError = "";

        // Candidates offered when a location search matched more than one place.
        private List<BomLocation> _matches = new();

        private JsonObject? _cachedWeather;
        private string _cachedGeohash = "";
        private DateTime _cachedAt;

        private sealed record BomLocation(string Name, string State, string Postcode, string Geohash)
        {
            public string DisplayName => string.IsNullOrEmpty(State) ? Name : $"{Name}, {State}";
            public string Title => string.IsNullOrEmpty(Postcode) ? DisplayName : $"{DisplayName} {Postcode}";
        }

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

        private string[] StateParts => state.Split(StateSep);

        private string Suburb => StateParts[0];

        private int Page
        {
            get  => StateParts.Length > 1 && int.TryParse(StateParts[1], out var p) ? p : 0;
            set  => state = EncodeState(Suburb, value, Geohash);
        }

        private string Geohash => StateParts.Length > 2 ? StateParts[2] : "";

        private void SetLocation(BomLocation location)
            => state = EncodeState(location.DisplayName, 0, location.Geohash);

        private static string EncodeState(string suburb, int page, string geohash)
            => $"{suburb}{StateSep}{page}{StateSep}{geohash}";

        // ── Template ─────────────────────────────────────────────────────────

        private static string GetDefaultTemplate()
        {
            if (string.IsNullOrEmpty(WidgetTemplate))
                WidgetTemplate = ReadPackageFileFromUri("ms-appx:///Templates/WeatherWidgetTemplate.json");
            return WidgetTemplate;
        }

        public override string GetTemplateForWidget() => GetDefaultTemplate();

        // ── Data ─────────────────────────────────────────────────────────────

        public override string GetDataForWidget() => GetData(forceRefresh: false);

        private string GetData(bool forceRefresh)
        {
            if (string.IsNullOrWhiteSpace(Suburb))
                return BuildPayload(BuildEmptyData());

            JsonObject weather;
            try
            {
                // Saved before locations were stored by geohash: look it up once by name.
                if (Geohash == "")
                {
                    var found = SearchLocationsAsync(Suburb).GetAwaiter().GetResult();
                    if (found.Count == 0)
                        return BuildPayload(BuildErrorData(Suburb, "Location not found. Tap 📍 Change to search again."));
                    var page = Page;
                    SetLocation(found[0]);
                    Page = page;
                }

                var lightTheme = IsLightTheme();
                var cacheValid = _cachedWeather != null
                              && _cachedGeohash == Geohash
                              && _cachedWeather["lightTheme"]?.GetValue<bool>() == lightTheme
                              && DateTime.UtcNow - _cachedAt < CacheLifetime;

                if (forceRefresh || !cacheValid)
                {
                    weather = FetchBomWeatherAsync(Geohash, Suburb, lightTheme).GetAwaiter().GetResult();
                    _cachedWeather = weather;
                    _cachedGeohash = Geohash;
                    _cachedAt      = DateTime.UtcNow;
                }
                else
                {
                    weather = _cachedWeather!;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WeatherWidget] Error: {ex.Message}");
                weather = BuildErrorData(Suburb, "Could not fetch BOM data. Check your connection and try again.");
            }

            return BuildPayload(weather);
        }

        // Adds the per-view fields (page, edit mode) to a copy of the weather data.
        private string BuildPayload(JsonObject weather)
        {
            var payload = (JsonObject)weather.DeepClone();
            payload["page"]       = Page;
            payload["editing"]    = _editing;
            payload["editError"]  = _editError;
            payload["hasMatches"] = _matches.Count > 0;
            payload["matches"]    = JsonSerializer.SerializeToNode(
                _matches.Select(m => new { title = m.Title, value = m.Geohash }));
            return payload.ToJsonString();
        }

        // ── Actions ───────────────────────────────────────────────────────────

        public override void OnActionInvoked(WidgetActionInvokedArgs args)
        {
            switch (args.Verb)
            {
                case "setLocation":
                {
                    var query = JsonNode.Parse(args.Data)?["suburb"]?.GetValue<string>()?.Trim() ?? "";
                    if (query == "")
                        break;

                    List<BomLocation> found;
                    try
                    {
                        found = SearchLocationsAsync(query).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WeatherWidget] Location search error: {ex.Message}");
                        _editing   = true;
                        _editError = "Could not reach the Bureau of Meteorology. Check your connection and try again.";
                        PushUpdate();
                        break;
                    }

                    if (found.Count == 1)
                    {
                        UseLocation(found[0]);
                    }
                    else
                    {
                        _editing   = true;
                        _matches   = found;
                        _editError = found.Count == 0
                            ? $"No location matches \"{query}\". Try a suburb name or postcode."
                            : "";
                        PushUpdate();
                    }
                    break;
                }

                case "pickLocation":
                {
                    var geohash = JsonNode.Parse(args.Data)?["pick"]?.GetValue<string>() ?? "";
                    var picked  = _matches.FirstOrDefault(m => m.Geohash == geohash);
                    if (picked != null)
                        UseLocation(picked);
                    break;
                }

                case "backToSearch":
                    _matches.Clear();
                    PushUpdate();
                    break;

                case "editLocation":
                    _editing = true;
                    PushUpdate();
                    break;

                case "cancelEdit":
                    _editing   = false;
                    _editError = "";
                    _matches.Clear();
                    PushUpdate();
                    break;

                case "showNow":    Page = 0; PushUpdate(); break;
                case "showDaily":  Page = 1; PushUpdate(); break;
                case "showHourly": Page = 2; PushUpdate(); break;
            }
        }

        private void UseLocation(BomLocation location)
        {
            SetLocation(location);
            _editing   = false;
            _editError = "";
            _matches.Clear();
            Console.WriteLine($"[WeatherWidget] Location set to: {location.Title} ({location.Geohash})");
            PushUpdate(forceRefresh: true);
        }

        public override void Activate(WidgetContext widgetContext)
        {
            isActivated = true;
            if (!string.IsNullOrWhiteSpace(Suburb))
                PushUpdate();

            // Refresh every 30 minutes — BOM publishes new observations on the half-hour.
            _refreshTimer?.Dispose();
            _refreshTimer = new Timer(
                _ => { if (isActivated) PushUpdate(forceRefresh: true); },
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

        private void PushUpdate(bool forceRefresh = false)
        {
            var options = new WidgetUpdateRequestOptions(Id)
            {
                Template    = GetTemplateForWidget(),
                Data        = GetData(forceRefresh),
                CustomState = State
            };
            WidgetManager.GetDefault().UpdateWidget(options);
        }

        // ── BOM fetch ─────────────────────────────────────────────────────────

        // Turns free text such as "Burwood", "Burwood NSW", "Scoresby, VIC" or "2134"
        // into matching BOM locations. BOM's search only understands a bare name or a
        // postcode, so a trailing state or postcode is split off and used as a filter.
        // When several places match, exact name matches are preferred
        // ("Burwood" → Burwood VIC and Burwood NSW, not Burwood East).
        private static async Task<List<BomLocation>> SearchLocationsAsync(string query)
        {
            var tokens = query.Replace(",", " ")
                              .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                              .ToList();

            string? stateFilter = null, postcodeFilter = null;
            while (tokens.Count > 0)
            {
                var last = tokens[^1];
                if (stateFilter == null && AuStates.Contains(last))
                    stateFilter = last.ToUpperInvariant();
                else if (postcodeFilter == null && last.Length == 4 && last.All(char.IsDigit))
                    postcodeFilter = last;
                else
                    break;
                tokens.RemoveAt(tokens.Count - 1);
            }

            var name   = string.Join(' ', tokens);
            var search = name != "" ? name : postcodeFilter;
            if (string.IsNullOrEmpty(search))
                return new List<BomLocation>();

            var json = JsonNode.Parse(await HttpClient.GetStringAsync(
                $"{BomApiBase}/locations?search={Uri.EscapeDataString(search)}"));

            var results = (json?["data"]?.AsArray() ?? new JsonArray())
                .Select(l =>
                {
                    var hash = l?["geohash"]?.GetValue<string>() ?? "";
                    return new BomLocation(
                        l?["name"]?.GetValue<string>() ?? "",
                        l?["state"]?.GetValue<string>() ?? "",
                        l?["postcode"]?.GetValue<string>() ?? "",
                        hash.Length > 6 ? hash[..6] : hash);
                })
                .Where(l => l.Geohash != "")
                .Where(l => stateFilter == null || string.Equals(l.State, stateFilter, StringComparison.OrdinalIgnoreCase))
                .Where(l => postcodeFilter == null || l.Postcode == postcodeFilter)
                .ToList();

            var exact = results.Where(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0)
                results = exact;

            return results.Take(MaxMatches).ToList();
        }

        private static async Task<JsonObject> FetchBomWeatherAsync(string geohash, string displaySuburb, bool lightTheme)
        {
            // Current observations
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

            // 3. Daily forecast (next 5 days)
            var dailyForecasts  = new List<object>();
            var hourlyForecasts = new List<object>();
            var todayIconDesc   = "";   // captured from today's forecast for the current-conditions page
            var todaySummary    = "";
            var todayMax        = "";
            var todayMin        = "";

            try
            {
                var fcJson = JsonNode.Parse(
                    await HttpClient.GetStringAsync($"{BomApiBase}/locations/{geohash}/forecasts/daily"));
                var days = fcJson?["data"]?.AsArray();
                if (days != null)
                {
                    var today = DateTime.Today;
                    var first = true;
                    foreach (var d in days.Take(5))
                    {
                        var dateStr  = d?["date"]?.GetValue<string>() ?? "";
                        var dayLabel = "Today";
                        if (DateTimeOffset.TryParse(dateStr, out var dt))
                        {
                            var local = dt.LocalDateTime.Date;
                            dayLabel = local == today ? "Today" : local.ToString("ddd");
                        }

                        var iconDesc = d?["icon_descriptor"]?.GetValue<string>() ?? "";
                        var desc     = (d?["short_text"]?.GetValue<string>() ?? "").TrimEnd('.');
                        var maxTemp  = FormatTemp(d?["temp_max"]?.GetValue<double?>());
                        var minTemp  = FormatTemp(d?["temp_min"]?.GetValue<double?>());

                        if (first)
                        {
                            todayIconDesc = iconDesc;
                            todaySummary  = desc;
                            todayMax      = maxTemp;
                            todayMin      = minTemp;
                            first = false;
                        }

                        dailyForecasts.Add(new
                        {
                            dayLabel,
                            iconUrl     = GetIconUrl(iconDesc),
                            desc,
                            maxTemp,
                            minTemp,
                            rainChance  = FormatChance(d?["rain"]?["chance"]?.GetValue<int?>())
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WeatherWidget] Daily forecast error: {ex.Message}");
            }

            // 4. Hourly forecast (next 8 hours)
            try
            {
                var hrJson = JsonNode.Parse(
                    await HttpClient.GetStringAsync($"{BomApiBase}/locations/{geohash}/forecasts/hourly"));
                var hours = hrJson?["data"]?.AsArray();
                if (hours != null)
                {
                    foreach (var h in hours.Take(8))
                    {
                        var timeStr  = h?["time"]?.GetValue<string>() ?? "";
                        var timeLabel = DateTimeOffset.TryParse(timeStr, out var dt)
                            ? dt.LocalDateTime.ToString("h tt").ToLowerInvariant().Replace(" ", "")   // e.g. "2pm"
                            : "";

                        var iconDesc = h?["icon_descriptor"]?.GetValue<string>() ?? "";
                        var hWind    = h?["wind"]?["speed_kilometre"]?.GetValue<double?>();
                        var hWindDir = h?["wind"]?["direction"]?.GetValue<string>() ?? "";

                        hourlyForecasts.Add(new
                        {
                            timeLabel,
                            iconUrl    = GetIconUrl(iconDesc),
                            temp       = FormatTemp(h?["temp"]?.GetValue<double?>()),
                            rainChance = FormatChance(h?["rain"]?["chance"]?.GetValue<int?>()),
                            wind       = hWind.HasValue ? $"{(int)hWind.Value} km/h {hWindDir}".Trim() : ""
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
            var backgroundDataUri = await GetBackgroundDataUriAsync(todayIconDesc, lightTheme);

            return JsonSerializer.SerializeToNode(new
            {
                hasData           = true,
                lightTheme,
                suburb            = displaySuburb,
                iconUrl           = GetIconUrl(todayIconDesc),
                backgroundImageUrl = backgroundDataUri,
                headerIconUrl     = GetHeaderIconUrl(todayIconDesc),
                // Current (page 0)
                temperature       = FormatTemp(temp),
                feelsLike         = FormatTemp(feelsLike),
                summary           = todaySummary,
                todayMax,
                todayMin,
                humidity          = humidity.HasValue ? $"{(int)humidity.Value}%" : "",
                rain              = rainSince9am.HasValue ? $"{rainSince9am.Value:0.#} mm" : "",
                wind              = windKmh.HasValue ? $"{(int)windKmh.Value} km/h {windDir}".Trim() : "",
                stationName,
                stationDist       = stationDistKm,
                // Forecast pages
                dailyForecasts,
                hourlyForecasts,
                hourlyStrip       = hourlyForecasts.Take(5),
                warnings,
                hasWarnings       = warnings.Count > 0,
                error             = ""
            })!.AsObject();
        }

        // ── Static helpers ────────────────────────────────────────────────────

        private static string FormatTemp(double? val)
            => val.HasValue ? $"{val.Value:0.#}" : "";

        // Chances under 5% are noise; leave the column blank rather than showing "0%".
        private static string FormatChance(int? chance)
            => chance is int c && c >= 5 ? $"{c}%" : "";

        private static JsonObject BuildEmptyData() => BuildErrorData("", "", hasData: false);

        private static JsonObject BuildErrorData(string suburb, string error, bool hasData = true) =>
            JsonSerializer.SerializeToNode(new
            {
                hasData,
                suburb, iconUrl = "", backgroundImageUrl = "", headerIconUrl = "",
                temperature = "", feelsLike = "", summary = "", todayMax = "", todayMin = "",
                humidity = "", rain = "", wind = "", stationName = "", stationDist = "",
                dailyForecasts = Array.Empty<object>(),
                hourlyForecasts = Array.Empty<object>(),
                hourlyStrip = Array.Empty<object>(),
                warnings = Array.Empty<object>(), hasWarnings = false,
                error
            })!.AsObject();

        // ── Image URL helpers ─────────────────────────────────────────────────
        //
        // Icons  : OpenWeatherMap PNG icons — free, no API key, reliable HTTPS.
        //          Used for Image elements in the card body and forecast rows.
        //
        // Backgrounds: Bundled package assets under Assets/WeatherBackgrounds/
        //   (sunny, partly_cloudy, cloudy, rain, storm, snow, fog, night .jpg).
        //   Each is downscaled and tinted to match the Windows theme before being
        //   sent, so text stays readable on top of it. If a file is missing the
        //   background simply won't render — the widget still works fine.
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

        // Reads the bundled background JPEG from the provider's own package,
        // downscales it, and lays a gradient over it that matches the Windows
        // theme: dark in dark mode, light in light mode. Text then uses the
        // theme's normal colour (including the header, which the Widget Host
        // draws itself and the card can't recolour) and stays readable however
        // bright or busy the photo is.
        // Results are cached — each image is only processed once per theme per run.
        private static async Task<string> GetBackgroundDataUriAsync(string descriptor, bool lightTheme)
        {
            var key      = GetBgKey(descriptor);
            var cacheKey = $"{key}|{(lightTheme ? "light" : "dark")}";

            lock (BgCache)
                if (BgCache.TryGetValue(cacheKey, out var cached))
                    return cached;

            var dataUri = "";
            try
            {
                var filePath = Path.Combine(
                    AppContext.BaseDirectory,
                    "Assets", "WeatherBackgrounds", $"{key}.jpg");

                if (File.Exists(filePath))
                {
                    var bytes     = await File.ReadAllBytesAsync(filePath);
                    var processed = ApplyScrim(bytes, lightTheme);
                    dataUri = $"data:image/jpeg;base64,{Convert.ToBase64String(processed)}";
                    Console.WriteLine($"[WeatherWidget] Background cached: {cacheKey} ({processed.Length / 1024} KB)");
                }
                else
                {
                    Console.WriteLine($"[WeatherWidget] Background not found: {filePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WeatherWidget] Background load failed for '{cacheKey}': {ex.Message}");
            }

            lock (BgCache)
                BgCache[cacheKey] = dataUri;
            return dataUri;
        }

        private static byte[] ApplyScrim(byte[] source, bool lightTheme)
        {
            using var ms  = new MemoryStream(source);
            using var src = new Bitmap(ms);

            var width  = Math.Min(src.Width, BgMaxWidth);
            var height = Math.Max(1, src.Height * width / src.Width);

            using var dst = new Bitmap(width, height);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, width, height);

                // Dark: ~45% black at the top rising to ~65% at the bottom.
                // Light: ~65% white rising to ~78%, since dark text needs a paler base.
                var (tint, topAlpha, bottomAlpha) = lightTheme
                    ? (Color.FromArgb(243, 243, 243), 165, 200)
                    : (Color.Black, 115, 165);

                using var scrim = new LinearGradientBrush(
                    new Rectangle(0, 0, width, height),
                    Color.FromArgb(topAlpha, tint),
                    Color.FromArgb(bottomAlpha, tint),
                    LinearGradientMode.Vertical);
                g.FillRectangle(scrim, 0, 0, width, height);
            }

            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);

            using var outStream = new MemoryStream();
            dst.Save(outStream, jpeg, encoderParams);
            return outStream.ToArray();
        }

        // The Widgets board follows the Windows app theme (Settings > Personalisation > Colours).
        private static bool IsLightTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v ? v != 0 : true;
            }
            catch
            {
                return true;
            }
        }

        // Used in the Adaptive Card 'header' property — must be an HTTPS URL
        // because it is resolved by the Widget Host, not the provider process.
        private static string GetHeaderIconUrl(string descriptor) => GetIconUrl(descriptor);
    }
}
