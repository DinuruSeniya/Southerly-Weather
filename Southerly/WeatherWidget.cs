// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Windows.Widgets.Providers;
using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Data.Xml.Dom;
using Windows.Devices.Geolocation;
using Windows.UI.Notifications;

namespace Southerly;

/// <summary>One pinned Southerly widget: its location, tab and edit state, and the BOM data it shows.</summary>
internal sealed class WeatherWidget
{
    public const string DefinitionId = "Southerly_Weather";

    private const string Api = "https://api.weather.bom.gov.au/v1";
    private const string Sep = "|||";   // custom state is "suburb|||page|||geohash"
    private const int MaxMatches = 6;

    // BOM publishes observations on the half-hour.
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private static readonly string[] States = ["VIC", "NSW", "QLD", "SA", "WA", "TAS", "NT", "ACT"];

    private static readonly string AssetDir = Path.Combine(AppContext.BaseDirectory, "Assets");
    private static readonly string Template =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates", "WeatherWidgetTemplate.json"));
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "Southerly/1.0" } }
    };
    private static readonly Dictionary<string, string> DataUris = new();

    // BOM icon_descriptor -> icon file in Assets/Icons (unlisted descriptors are "cloudy").
    private static readonly Dictionary<string, string> Icons = new()
    {
        ["sunny"] = "sunny", ["clear"] = "clear", ["mostly_sunny"] = "partly", ["partly_cloudy"] = "partly",
        ["hazy"] = "haze", ["dusty"] = "dust", ["fog"] = "fog", ["frost"] = "frost", ["snow"] = "snow",
        ["light_shower"] = "shower", ["shower"] = "shower", ["light_rain"] = "shower",
        ["rain"] = "rain", ["heavy_shower"] = "rain", ["heavy_rain"] = "rain",
        ["storm"] = "storm", ["wind"] = "wind", ["cyclone"] = "wind",
    };

    // Icon -> background photo in Assets/Backgrounds (others use "cloudy").
    private static readonly Dictionary<string, string> Photos = new()
    {
        ["sunny"] = "sunny", ["partly"] = "partly_cloudy", ["clear"] = "night", ["partly_night"] = "night",
        ["shower"] = "rain", ["rain"] = "rain", ["storm"] = "storm", ["snow"] = "snow", ["frost"] = "snow", ["fog"] = "fog",
    };

    private sealed record Place(string Name, string State, string Postcode, string Geohash)
    {
        public string Display => State == "" ? Name : $"{Name}, {State}";
        public string Title => Postcode == "" ? Display : $"{Display} {Postcode}";
    }

    private readonly string _id;
    private readonly object _gate = new();
    private string _suburb = "", _geohash = "";
    private int _page;                       // 0 now, 1 daily, 2 hourly
    private bool _editing, _stale;
    private string _editError = "";
    private List<Place> _matches = [];
    private JsonObject? _weather;            // last fetch for _geohash
    private DateTime _loadedAt;
    private Timer? _timer;

    public WeatherWidget(string id, string state)
    {
        _id = id;
        var p = state.Split(Sep);
        if (p.Length == 3)
            (_suburb, _page, _geohash) = (p[0], int.TryParse(p[1], out var n) ? n : 0, p[2]);
    }

    public string State => $"{_suburb}{Sep}{_page}{Sep}{_geohash}";

    // ── Host callbacks ────────────────────────────────────────────────────

    public void Activate()
    {
        Push(_stale || DateTime.UtcNow - _loadedAt > StaleAfter);
        _timer ??= new Timer(_ =>
        {
            try { Push(true); }
            catch (Exception ex) { Console.WriteLine($"Refresh failed: {ex.Message}"); }
        }, null, RefreshEvery, RefreshEvery);
    }

    public void Deactivate()
    {
        _timer?.Dispose();
        _timer = null;
    }

    // "Customize widget" in the ⋯ menu: the only way to change place on the small size.
    public void Customize()
    {
        lock (_gate)
        {
            _matches.Clear();
            Edit("");
        }
    }

    public void OnAction(WidgetActionInvokedArgs args)
    {
        lock (_gate)
        {
            switch (args.Verb)
            {
                case "setLocation": Search(Input(args, "suburb")); break;
                case "pickLocation": Use(_matches.FirstOrDefault(m => m.Geohash == Input(args, "pick"))); break;
                case "useMyLocation": UseMyLocation(); break;
                case "editLocation": Edit(""); break;
                case "backToSearch": _matches.Clear(); Push(); break;
                case "cancelEdit": (_editing, _editError) = (false, ""); _matches.Clear(); Push(); break;
                case "showNow": case "showDaily": case "showHourly":
                    _page = args.Verb switch { "showDaily" => 1, "showHourly" => 2, _ => 0 };
                    Push();
                    break;
            }
        }
    }

    private static string Input(WidgetActionInvokedArgs args, string name)
        => string.IsNullOrEmpty(args.Data) ? "" : ((string?)JsonNode.Parse(args.Data)?[name])?.Trim() ?? "";

    // ── Location ──────────────────────────────────────────────────────────

    private void Edit(string error)
    {
        (_editing, _editError) = (true, error);
        Push();
    }

    private void Use(Place? place)
    {
        if (place == null) return;
        (_suburb, _geohash, _page) = (place.Display, place.Geohash, 0);
        (_weather, _editing, _editError) = (null, false, "");
        _matches.Clear();
        Push(true);
    }

    private void Search(string query)
    {
        if (query == "") return;
        List<Place> found;
        try { found = SearchAsync(query).GetAwaiter().GetResult(); }
        catch { Edit("Could not reach the Bureau of Meteorology. Check your connection and try again."); return; }

        if (found.Count == 1)
        {
            Use(found[0]);
            return;
        }
        _matches = found;
        Edit(found.Count == 0 ? $"No location matches \"{query}\". Try a suburb name or postcode." : "");
    }

    private void UseMyLocation()
    {
        try { Use(LocateAsync().GetAwaiter().GetResult()); }
        catch { Edit("Couldn't find you. Check Windows location settings, or search for a suburb."); }
    }

    // Turns free text such as "Burwood", "Burwood NSW", "Scoresby, VIC" or "2134" into BOM
    // locations. BOM's search only understands a bare name or a postcode, so a trailing
    // state or postcode is split off and used as a filter. Exact name matches win
    // ("Burwood" -> Burwood VIC and Burwood NSW, not Burwood East).
    private static async Task<List<Place>> SearchAsync(string query)
    {
        var words = query.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        string? state = null, postcode = null;
        for (; words.Count > 0; words.RemoveAt(words.Count - 1))
        {
            var w = words[^1];
            if (state == null && States.Contains(w, StringComparer.OrdinalIgnoreCase)) state = w.ToUpperInvariant();
            else if (postcode == null && w.Length == 4 && w.All(char.IsDigit)) postcode = w;
            else break;
        }

        var name = string.Join(' ', words);
        if (name == "" && postcode == null) return [];

        var found = ((await Get($"/locations?search={Uri.EscapeDataString(name == "" ? postcode! : name)}"))?["data"]?.AsArray() ?? [])
            .Select(l => new Place((string?)l?["name"] ?? "", (string?)l?["state"] ?? "", (string?)l?["postcode"] ?? "",
                ((string?)l?["geohash"] ?? "")[..Math.Min(6, ((string?)l?["geohash"] ?? "").Length)]))
            .Where(p => p.Geohash != ""
                     && (state == null || p.State.Equals(state, StringComparison.OrdinalIgnoreCase))
                     && (postcode == null || p.Postcode == postcode))
            .ToList();

        var exact = found.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return (exact.Count > 0 ? exact : found).Take(MaxMatches).ToList();
    }

    private static async Task<Place> LocateAsync()
    {
        if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed)
            throw new UnauthorizedAccessException();
        var fix = await new Geolocator { DesiredAccuracyInMeters = 5000 }
            .GetGeopositionAsync(TimeSpan.FromHours(1), TimeSpan.FromSeconds(10));
        var hash = Geohash(fix.Coordinate.Point.Position.Latitude, fix.Coordinate.Point.Position.Longitude);

        // BOM answers 404 outside Australia, which lands in the caller's catch.
        var place = (await Get($"/locations/{hash}"))?["data"];
        return new Place((string)place!["name"]!, (string?)place["state"] ?? "", "", hash);
    }

    private static string Geohash(double lat, double lon)
    {
        double[] latRange = [-90, 90], lonRange = [-180, 180];
        var hash = "";
        for (int bits = 0, ch = 0; hash.Length < 6; bits++)
        {
            var (range, value) = bits % 2 == 0 ? (lonRange, lon) : (latRange, lat);
            var mid = (range[0] + range[1]) / 2;
            ch = ch * 2 + (value >= mid ? 1 : 0);
            range[value >= mid ? 0 : 1] = mid;
            if (bits % 5 == 4) { hash += "0123456789bcdefghjkmnpqrstuvwxyz"[ch & 31]; ch = 0; }
        }
        return hash;
    }

    // ── Pushing to the host ───────────────────────────────────────────────

    public void Push(bool refresh = false)
    {
        lock (_gate)
        {
            Load(refresh);
            WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(_id)
            {
                Template = Template,
                Data = Payload(),
                CustomState = State
            });
        }
    }

    // Tab taps reuse what is already loaded; only the timer, Activate and a new place fetch.
    private void Load(bool refresh)
    {
        if (_geohash == "" || (!refresh && _weather != null)) return;
        try
        {
            var fresh = Fetch(_geohash, _suburb).GetAwaiter().GetResult();
            WarnAboutNew(_weather, fresh);
            (_weather, _loadedAt, _stale) = (fresh, DateTime.UtcNow, false);
        }
        catch (Exception ex)
        {
            // Keep showing the last good data, marked as offline.
            Console.WriteLine($"Fetch failed: {ex.Message}");
            _stale = true;
        }
    }

    private string Payload()
    {
        var p = _weather?.DeepClone().AsObject() ?? new JsonObject { ["suburb"] = _suburb, ["iconUrl"] = "" };
        var light = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not 0;

        p["hasData"] = _geohash != "";
        p["error"] = _geohash != "" && _weather == null ? "Could not reach the Bureau of Meteorology. Check your connection and try again." : "";
        p["page"] = _page;
        p["editing"] = _editing;
        p["editError"] = _editError;
        p["hasMatches"] = _matches.Count > 0;
        p["matches"] = JsonSerializer.SerializeToNode(_matches.Select(m => new { title = m.Title, value = m.Geohash }));
        if (_weather != null)
        {
            p["backgroundImageUrl"] = DataUri($"Backgrounds/{p["photo"]}_{(light ? "light" : "dark")}.jpg");
            p["stationLine"] = _stale ? $"⚠ Offline · updated {p["observedAt"]}" : p["station"]!.ToString();
        }
        return p.ToJsonString();
    }

    // ── BOM ───────────────────────────────────────────────────────────────

    private static async Task<JsonNode?> Get(string path) => JsonNode.Parse(await Http.GetStringAsync(Api + path));

    private static async Task<JsonNode?> TryGet(string path)
    {
        try { return await Get(path); }
        catch (Exception ex) { Console.WriteLine($"{path}: {ex.Message}"); return null; }
    }

    // Observations are required; the rest degrade to blanks if BOM is slow or failing.
    private static async Task<JsonObject> Fetch(string geohash, string suburb)
    {
        var obsTask = Get($"/locations/{geohash}/observations");
        var infoTask = TryGet($"/locations/{geohash}");
        var dailyTask = TryGet($"/locations/{geohash}/forecasts/daily");
        var hourlyTask = TryGet($"/locations/{geohash}/forecasts/hourly");
        var warningsTask = TryGet($"/locations/{geohash}/warnings");

        var obsRoot = await obsTask;
        var obs = obsRoot?["data"];
        var info = (await infoTask)?["data"];
        var days = (await dailyTask)?["data"]?.AsArray() ?? [];
        var hours = (await hourlyTask)?["data"]?.AsArray() ?? [];
        var warned = (await warningsTask)?["data"]?.AsArray() ?? [];

        // Times and day names are the place's, not the PC's.
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById((string?)info?["timezone"] ?? ""); }
        catch { zone = TimeZoneInfo.Local; }
        string Clock(JsonNode? iso, string format = "h:mmtt") =>
            DateTimeOffset.TryParse((string?)iso, out var t)
                ? TimeZoneInfo.ConvertTime(t, zone).ToString(format, CultureInfo.InvariantCulture).ToLowerInvariant()
                : "";
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;

        var first = days.FirstOrDefault();
        var night = (bool?)first?["now"]?["is_night"] == true;
        var icon = IconKey((string?)first?["icon_descriptor"], night);
        // BOM drops today's low once it has passed; its "now" block still carries tonight's.
        var now = first?["now"];
        var low = Num(first?["temp_min"]);
        if (low == "")
            low = (string?)now?["now_label"] == "Overnight min" ? Num(now?["temp_now"])
                : (string?)now?["later_label"] == "Overnight min" ? Num(now?["temp_later"]) : "";

        var amount = first?["rain"]?["amount"];
        var rainText = Chance(first?["rain"]?["chance"]) is var chance and not ""
            ? $"{chance} chance of rain" + ((double?)amount?["max"] > 0
                ? $" · {((double?)amount?["min"] > 0 ? Num(amount?["min"]) + "–" : "up to ")}{Num(amount?["max"])} mm" : "")
            : "";

        var uv = first?["uv"];
        var uvText = (int?)uv?["max_index"] is int index
            && DateTimeOffset.TryParse((string?)uv?["end_time"], out var uvEnd) && uvEnd > DateTimeOffset.UtcNow
            ? $"UV {index} {Cap((string?)uv?["category"])}" + (index >= 3 ? $" · protect {Clock(uv?["start_time"])}–{Clock(uv?["end_time"])}" : "")
            : "";

        var fire = (string?)first?["fire_danger_category"]?["text"] ?? "";
        if (Regex.IsMatch(fire, "^(no rating)?$", RegexOptions.IgnoreCase)) fire = "";

        // What matters most today, most important first. Medium shows one, Large two.
        var notes = new List<object>();
        if (fire != "")
            notes.Add(new { text = $"Fire danger: {fire}", color = Regex.IsMatch(fire, "catastrophic|extreme|severe", RegexOptions.IgnoreCase) ? "attention"
                                                                  : Regex.IsMatch(fire, "high", RegexOptions.IgnoreCase) ? "warning" : "good" });
        if (uvText != "") notes.Add(new { text = uvText, color = "default" });
        if (rainText != "") notes.Add(new { text = rainText, color = "default" });

        var hourly = hours.Take(8).Select(hr => new
        {
            timeLabel = Clock(hr?["time"], "htt"),
            iconUrl = Icon(IconKey((string?)hr?["icon_descriptor"], (bool?)hr?["is_night"] == true)),
            temp = Num(hr?["temp"]),
            rainChance = Chance(hr?["rain"]?["chance"]),
            wind = Wind(hr?["wind"])
        }).ToList();

        var warnings = warned
            .Select(w => new { title = (string?)w?["title"] ?? "", major = (string?)w?["warning_group_type"] != "minor" })
            .Where(w => w.title != "")
            .OrderByDescending(w => w.major)
            .Take(2)
            .ToList();

        var name = (string?)info?["name"] ?? suburb.Split(',')[0];
        var distance = (double?)obs?["station"]?["distance"];

        return JsonSerializer.SerializeToNode(new
        {
            suburb,
            iconUrl = Icon(icon),
            photo = Photos.GetValueOrDefault(icon, "cloudy"),
            temperature = Num(obs?["temp"]),
            feelsLike = Num(obs?["temp_feels_like"]),
            summary = ((string?)first?["short_text"] ?? "").TrimEnd('.'),
            todayMax = Num(first?["temp_max"]),
            todayMin = low,
            todayRain = Chance(first?["rain"]?["chance"]),
            notes = notes.Take(2),
            humidity = (double?)obs?["humidity"] is double h ? $"{(int)h}%" : "",
            rain = (double?)obs?["rain_since_9am"] is double r ? $"{r:0.#} mm" : "",
            wind = Wind(obs?["wind"]),
            station = (string?)obs?["station"]?["name"] + (distance is double m ? $" · {m / 1000:0.#} km" : ""),
            observedAt = Clock(obsRoot?["metadata"]?["observation_time"]),
            bomUrl = $"https://weather.bom.gov.au/location/{geohash}-{string.Join('-', name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))}",
            dailyForecasts = days.Take(5).Select(d => new
            {
                dayLabel = DateTimeOffset.TryParse((string?)d?["date"], out var date) && TimeZoneInfo.ConvertTime(date, zone).Date != today
                    ? TimeZoneInfo.ConvertTime(date, zone).ToString("ddd", CultureInfo.InvariantCulture) : "Today",
                iconUrl = Icon(IconKey((string?)d?["icon_descriptor"], false)),
                desc = ((string?)d?["short_text"] ?? "").TrimEnd('.'),
                maxTemp = Num(d?["temp_max"]),
                minTemp = Num(d?["temp_min"]),
                rainChance = Chance(d?["rain"]?["chance"])
            }),
            hourlyForecasts = hourly,
            hourlyStrip = hourly.Take(5),
            warnings,
            hasWarnings = warnings.Count > 0
        })!.AsObject();
    }

    private static string Num(JsonNode? n) => (double?)n is double v ? v.ToString("0.#", CultureInfo.InvariantCulture) : "";

    // Chances under 5% are noise; leave them blank rather than showing "0%".
    private static string Chance(JsonNode? n) => (int?)n is int c && c >= 5 ? $"{c}%" : "";

    private static string Wind(JsonNode? w)
        => (double?)w?["speed_kilometre"] is double kmh ? $"{(int)kmh} km/h {(string?)w?["direction"]}".Trim() : "";

    private static string Cap(string? s)
    {
        s = (s ?? "").Replace("very", "very ");
        return s == "" ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static string IconKey(string? descriptor, bool night)
    {
        var key = Icons.GetValueOrDefault(descriptor ?? "", "cloudy");
        return night ? key switch { "sunny" => "clear", "partly" => "partly_night", _ => key } : key;
    }

    private static string Icon(string key) => DataUri($"Icons/{key}.png");

    // Images are sent inline: the Widgets host can't read this package's files.
    private static string DataUri(string file)
    {
        lock (DataUris)
        {
            if (!DataUris.TryGetValue(file, out var uri))
            {
                try { uri = $"data:image/{(file.EndsWith(".png") ? "png" : "jpeg")};base64,{Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AssetDir, file)))}"; }
                catch (Exception ex) { Console.WriteLine($"Missing asset {file}: {ex.Message}"); uri = ""; }
                DataUris[file] = uri;
            }
            return uri;
        }
    }

    // Toast severe warnings that weren't in the previous fetch for this place.
    private static void WarnAboutNew(JsonObject? previous, JsonObject fresh)
    {
        if (previous == null) return;
        var known = previous["warnings"]!.AsArray().Select(w => (string?)w?["title"]).ToHashSet();
        foreach (var w in fresh["warnings"]!.AsArray().Where(w => (bool?)w?["major"] == true && !known.Contains((string?)w?["title"])))
        {
            try
            {
                var xml = new XmlDocument();
                xml.LoadXml($"<toast><visual><binding template='ToastGeneric'><text>Bureau of Meteorology warning</text><text>{SecurityElement.Escape((string?)w!["title"])}</text></binding></visual></toast>");
                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
            }
            catch (Exception ex) { Console.WriteLine($"Toast failed: {ex.Message}"); }
        }
    }
}
