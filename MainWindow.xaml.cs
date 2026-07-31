using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DascHUD
{
    public partial class MainWindow : Window
    {
        private Dictionary<string, StackPanel> _radarBlips = new Dictionary<string, StackPanel>();
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly DispatcherTimer _clockTimer;
        private AppConfig _config;
        private static readonly object LogLock = new object();

        public MainWindow()
        {
            InitializeComponent();

            // Catch any fatal background crashes and log them
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    LogError("FATAL UNHANDLED EXCEPTION", ex);
            };
            
            LoadConfig();
            PositionWindowOnTargetMonitor();
            ApplyHudLayout(); 

            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += (s, e) => UpdateClock();
            _clockTimer.Start();
            UpdateClock();
            
            WeatherText.Text = "WEATHER: Fetching...";
            DisasterText.Text = "";
            FlightText.Text = $"✈️ {_config.CityCode}: initializing Radar Link";
            EmergencyText.Text = "";
        }

private void LogError(string context, Exception ex)
        {
            // 1. FILTER NON-ACTIONABLE NOISE
            // Ignore generic timeouts and thread cancellations
            if (ex is TaskCanceledException || ex is TimeoutException) return;

            if (ex is HttpRequestException httpEx)
            {
                // Only log HTTP exceptions if they are actionable (Auth failed, Rate Limited, etc.)
                if (httpEx.StatusCode != System.Net.HttpStatusCode.Unauthorized && 
                    httpEx.StatusCode != System.Net.HttpStatusCode.Forbidden &&
                    httpEx.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                {
                    return; // Silently ignore routine 502/503/504 server drops or generic connection loss
                }
            }

            if (ex.InnerException is System.Net.Sockets.SocketException) return;

            // 2. LOG ACTIONABLE ERRORS
            try
            {
                lock (LogLock)
                {
                    string logPath = "dashud.log";
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 1_000_000)
                    {
                        File.Delete("dashud.log.old");
                        File.Move(logPath, "dashud.log.old");
                    }

                    string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}] {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}";
                    File.AppendAllText(logPath, logMessage);
                }
            }
            catch { }
        }

        private void LoadConfig()
        {
            string configPath = "config.json";
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                catch (Exception ex)
                {
                    _config = new AppConfig();
                    LogError("ConfigLoad", ex);
                }
            }
            else
            {
                _config = new AppConfig();
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(configPath, JsonSerializer.Serialize(_config, options));
            }
        }

        private void ApplyHudLayout()
        {
            HudContainer.Margin = new Thickness(
                _config.HudMarginLeft, 
                _config.HudMarginTop, 
                _config.HudMarginRight, 
                _config.HudMarginBottom);

            if (Enum.TryParse(_config.HudVerticalAlignment, true, out VerticalAlignment vAlign))
            {
                HudContainer.VerticalAlignment = vAlign;
            }

            // EXPLICIT NAMESPACE FIX: System.Windows.HorizontalAlignment
            if (Enum.TryParse(_config.HudHorizontalAlignment, true, out System.Windows.HorizontalAlignment hAlign))
            {
                HudContainer.HorizontalAlignment = hAlign;
                
                TextAlignment textAlign = hAlign switch
                {
                    System.Windows.HorizontalAlignment.Left => TextAlignment.Left,
                    System.Windows.HorizontalAlignment.Center => TextAlignment.Center,
                    _ => TextAlignment.Right
                };
                
                TimeText.TextAlignment = textAlign;
                DateText.TextAlignment = textAlign;
                WeatherText.TextAlignment = textAlign;
                DisasterText.TextAlignment = textAlign;
                EmergencyText.TextAlignment = textAlign;
                FlightText.TextAlignment = textAlign;
                SatelliteText.TextAlignment = textAlign;
                FireText.TextAlignment = textAlign;
                NotamText.TextAlignment = textAlign;

                TimeText.HorizontalAlignment = hAlign;
                DateText.HorizontalAlignment = hAlign;
                WeatherText.HorizontalAlignment = hAlign;
                DisasterText.HorizontalAlignment = hAlign;
                EmergencyText.HorizontalAlignment = hAlign;
                FlightText.HorizontalAlignment = hAlign;
                SatelliteText.HorizontalAlignment = hAlign;
                FireText.HorizontalAlignment = hAlign;
                NotamText.HorizontalAlignment = hAlign;
            }
        }

        private void PositionWindowOnTargetMonitor()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            int monitorIndex = _config.DisplayMonitor - 1;

            if (monitorIndex < 0 || monitorIndex >= screens.Length)
            {
                monitorIndex = 0; 
            }

            var targetScreen = screens[monitorIndex].Bounds;
            this.WindowStartupLocation = WindowStartupLocation.Manual;
            this.WindowState = WindowState.Normal;
            this.Left = targetScreen.Left;
            this.Top = targetScreen.Top;
            this.Width = targetScreen.Width;
            this.Height = targetScreen.Height;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
           /* _ = WeatherLoopAsync();
            _ = FlightLoopAsync();
            _ = DisasterLoopAsync();
            _ = SatelliteLoopAsync(); 
            _ = NotamLoopAsync(); // New background worker for airspace*/
            _ = RunBootSequenceAsync();
        }
private async Task RunBootSequenceAsync()
        {
            // Clear standard text blocks during boot
            WeatherText.Text = "";
            DisasterText.Text = "";
            EmergencyText.Text = "";
            SatelliteText.Text = "";
            FireText.Text = "";
            NotamText.Text = "";

            FlightText.Foreground = System.Windows.Media.Brushes.Gold;
            FlightText.Text = "[SYS] INITIATING DIAGNOSTICS...";
            await Task.Delay(800);

            var status = new List<string>();

            // 1. Check Global Network Connectivity
            bool networkUp = false;
            try 
            { 
                using var ping = new System.Net.NetworkInformation.Ping(); 
                networkUp = (await ping.SendPingAsync("8.8.8.8", 1000)).Status == System.Net.NetworkInformation.IPStatus.Success; 
            } 
            catch { }
            status.Add($"[UPLINK] {(networkUp ? "ESTABLISHED" : "OFFLINE")}");

            // 2. Validate API Key Configurations
            bool n2yoOk = !string.IsNullOrWhiteSpace(_config.N2YoApiKey) && _config.N2YoApiKey != "YOUR_N2YO_API_KEY_HERE";
            status.Add($"[ORBITAL] {(n2yoOk ? "AUTH OK" : "KEY MISSING")}");

            bool firmsOk = !string.IsNullOrWhiteSpace(_config.FirmsApiKey) && _config.FirmsApiKey != "YOUR_FIRMS_API_KEY";
            status.Add($"[THERMAL] {(firmsOk ? "AUTH OK" : "KEY MISSING")}");

            bool notamOk = !string.IsNullOrWhiteSpace(_config.SkylinkApiKey) && _config.SkylinkApiKey != "YOUR_RAPIDAPI_KEY";
            status.Add($"[AIRSPACE] {(notamOk ? "AUTH OK" : "KEY MISSING")}");

            // Display diagnostics on the HUD
            FlightText.Text = string.Join("\n", status);
            await Task.Delay(3500); // Hold for 3.5 seconds so you can read it

            // Reset and hand over to the operational loops
            FlightText.Foreground = System.Windows.Media.Brushes.White;
            FlightText.Text = $"✈️ {_config.CityCode}: establishing Radar Link...";
            WeatherText.Text = "WEATHER: Fetching...";

            _ = WeatherLoopAsync();
            _ = FlightLoopAsync();
            _ = DisasterLoopAsync();
            _ = SatelliteLoopAsync(); 
            _ = NotamLoopAsync(); 
        }
        private void UpdateClock()
        {
            DateTime now = DateTime.Now;
            TimeText.Text = now.ToString("HH:mm");
            DateText.Text = now.ToString("dd-MM-yyyy | dddd");
        }

        private async Task SatelliteLoopAsync()
        {
            while (true)
            {
                await FetchSatelliteDataAsync();
                await Task.Delay(TimeSpan.FromMinutes(1)); 
            }
        }

        private async Task NotamLoopAsync()
        {
            while (true)
            {
                await FetchNotamDataAsync();
                await Task.Delay(TimeSpan.FromHours(8)); // 8-hour polling to save API limits
            }
        }

        private async Task WeatherLoopAsync()
        {
            while (true)
            {
                await FetchWeatherDataAsync();
                await Task.Delay(TimeSpan.FromMinutes(60));
            }
        }

        private async Task DisasterLoopAsync()
        {
            while (true)
            {
                await FetchDisasterDataAsync();
                await Task.Delay(TimeSpan.FromMinutes(15));
            }
        }

        private async Task FlightLoopAsync()
        {
            while (true)
            {
                await FetchFlightDataAsync();
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }

        private async Task FetchWeatherDataAsync()
        {
            try
            {
                string url = $"https://api.open-meteo.com/v1/forecast?latitude={_config.Latitude}&longitude={_config.Longitude}&current_weather=true";
                string response = await Http.GetStringAsync(url);
                var meteoData = JsonSerializer.Deserialize<MeteoResponse>(response);

                if (meteoData != null && meteoData.CurrentWeather != null)
                {
                    double temp = meteoData.CurrentWeather.Temperature;
                    int code = meteoData.CurrentWeather.WeatherCode;
                    string condition = ParseWeatherCode(code);
                    
                    WeatherText.Text = $"WEATHER: {temp:F0}°C ({condition})";
                }
            }
            catch (Exception ex)
            {
                WeatherText.Text = "WEATHER: Offline";
                LogError("WeatherLoop", ex);
            }
        }

        private async Task FetchNotamDataAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.SkylinkApiKey) || _config.SkylinkApiKey == "YOUR_RAPIDAPI_KEY")
                return;

            try
            {
                var targetRegions = _config.NotamIcaos ?? new List<string> { "OPRR", "OPKR", "VABF", "VIDF" };
                var activeNotams = new List<string>();

                foreach (string icao in targetRegions)
                {
                    var request = new HttpRequestMessage
                    {
                        Method = HttpMethod.Get,
                        RequestUri = new Uri($"https://skylink-api.p.rapidapi.com/v3/notams/{icao}"),
                        Headers =
                        {
                            { "x-rapidapi-key", _config.SkylinkApiKey },
                            { "x-rapidapi-host", "skylink-api.p.rapidapi.com" },
                        }
                    };

                    using var response = await Http.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        string jsonString = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(jsonString);

                        if (doc.RootElement.TryGetProperty("notams", out var notamsArray) && notamsArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var notam in notamsArray.EnumerateArray())
                            {
                                string body = notam.TryGetProperty("body", out var bProp) ? bProp.GetString() ?? "" : "";
                                string upperBody = body.ToUpper();
                                
                                // Aggressive filter for kinetic events and airspace denial
                                if (upperBody.Contains("WEAPON") || 
                                    upperBody.Contains("MISSILE") || 
                                    upperBody.Contains("FIRING") || 
                                    upperBody.Contains("GUNNERY") || 
                                    upperBody.Contains("MILITARY EXERCISE") || 
                                    upperBody.Contains("AIR DEFENCE") ||
                                    upperBody.Contains("RESTRICTED AREA") ||
                                    upperBody.Contains("AIRSPACE CLOSED"))
                                {
                                    string id = notam.TryGetProperty("notam_id", out var idProp) ? idProp.GetString() ?? "UNK" : "UNK";
                                    
                                    string eventType = "RESTRICTION";
                                    if (upperBody.Contains("MISSILE") || upperBody.Contains("FIRING") || upperBody.Contains("WEAPON")) eventType = "LIVE FIRE";
                                    else if (upperBody.Contains("EXERCISE") || upperBody.Contains("AIR DEFENCE")) eventType = "MIL EXERCISE";
                                    
                                    string cleanBody = body.Replace("\n", " ").Replace("\r", "");
                                    string shortBody = cleanBody.Length > 75 ? cleanBody.Substring(0, 75) + "..." : cleanBody;
                                    
                                    string alert = $"📜 {icao} [{eventType}]: {shortBody}";
                                    if (!activeNotams.Contains(alert))
                                    {
                                        activeNotams.Add(alert);
                                    }
                                }
                            }
                        }
                    }
                }

                // Render to HUD
                if (activeNotams.Any())
                {
                    NotamText.Text = string.Join("\n", activeNotams.Take(3));
                    NotamText.Foreground = System.Windows.Media.Brushes.Gold;
                }
                else
                {
                    NotamText.Text = ""; 
                }
            }
            catch (Exception ex) { LogError("NotamLoop - SkyLink API", ex); }
        }
        
        private async Task FetchSatelliteDataAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.N2YoApiKey) || _config.N2YoApiKey == "YOUR_N2YO_API_KEY_HERE")
                return;

            var activeSatAlerts = new List<string>();

            // N2YO Categories: 
            // 30 = Military (US/RU/CN/IN Military)
            // 6 = Earth Resources (SUPARCO, Cartosat, civilian-operated Gov EO)
            // 2 = Int'l Space Station (ISS)
            // 54 = Chinese Space Station (Tiangong)
            // 52 = Starlink
            int[] targetCategories = { 30, 6, 2, 54, 52 }; 

            foreach (int categoryId in targetCategories)
            {
                try
                {
                    // Search parameters: Lat, Lon, Alt (0m), Search Radius (70 degrees overhead dome)
                    string domeUrl = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "https://api.n2yo.com/rest/v1/satellite/above/{0:F4}/{1:F4}/0/70/{2}/&apiKey={3}",
                        _config.Latitude, _config.Longitude, categoryId, _config.N2YoApiKey);

                    HttpResponseMessage response = await Http.GetAsync(domeUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        string jsonString = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(jsonString);

                        if (doc.RootElement.TryGetProperty("above", out var aboveArray) && aboveArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var sat in aboveArray.EnumerateArray().Take(2)) // Grab top 2 from each category
                            {
                                string satName = sat.TryGetProperty("satname", out var nProp) ? nProp.GetString()?.ToUpper() ?? "UNK" : "UNK";
                                double satAlt = sat.TryGetProperty("satalt", out var aProp) && aProp.ValueKind == JsonValueKind.Number ? aProp.GetDouble() : 0;

                                // Auto-tagging logic based on satellite nomenclature
                                string originTag = "[MIL/GOV]";
                                
                                if (satName.Contains("ISS") || satName.Contains("ZARYA") || satName.Contains("TIANGONG") || satName.Contains("CSS")) 
                                    originTag = "[STATION]";
                                else if (satName.Contains("STARLINK")) 
                                    originTag = "[STARLINK]"; 
                                else if (satName.Contains("PRSS") || satName.Contains("PAKTES")) 
                                    originTag = "[SUPARCO]";
                                else if (satName.Contains("USA") || satName.Contains("NROL") || satName.Contains("KH-")) 
                                    originTag = "[US DEF]";
                                else if (satName.Contains("YAOGAN") || satName.Contains("GAOFEN") || satName.Contains("SHIJIAN")) 
                                    originTag = "[CN DEF]";
                                else if (satName.Contains("KOSMOS") || satName.Contains("COSMOS")) 
                                    originTag = "[RU DEF]";
                                else if (satName.Contains("RISAT") || satName.Contains("CARTOSAT") || satName.Contains("EMISAT")) 
                                    originTag = "[IN DEF]";

                                string alert = $"🛰️ OVERHEAD: {satName} {originTag} | ALT: {satAlt:F0}km";
                                
                                if (!activeSatAlerts.Contains(alert))
                                {
                                    activeSatAlerts.Add(alert);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { LogError($"SatelliteLoop - Category {categoryId}", ex); }
            }

            // RENDER TO HUD DISASTER/ALERT PANEL
            if (activeSatAlerts.Any())
            {
                SatelliteText.Text = string.Join("\n", activeSatAlerts);
                SatelliteText.Foreground = System.Windows.Media.Brushes.Cyan;
            }
            else
            {
                SatelliteText.Text = ""; 
            }
        }

        private async Task FetchDisasterDataAsync()
        {
            var activeAlerts = new List<string>();

            // 4. THERMAL ANOMALY & FIRE MONITOR (NASA FIRMS) - 30km STRICT RADIUS
            try
            {
                FireText.Text = ""; 

                if (!string.IsNullOrWhiteSpace(_config.FirmsApiKey) && _config.FirmsApiKey != "YOUR_FIRMS_API_KEY")
                {
                    // Strict 30km radius converted to lat/lon offsets
                    double radiusKm = 30.0;
                    double latOffset = radiusKm / 111.0; 
                    double lonOffset = radiusKm / (111.0 * Math.Cos(_config.Latitude * Math.PI / 180.0));

                    // FIRMS expects: West, South, East, North
                    double west = _config.Longitude - lonOffset;
                    double south = _config.Latitude - latOffset;
                    double east = _config.Longitude + lonOffset;
                    double north = _config.Latitude + latOffset;

                    string firmsUrl = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "https://firms.modaps.eosdis.nasa.gov/api/area/csv/{0}/VIIRS_SNPP_NRT/{1:0.000},{2:0.000},{3:0.000},{4:0.000}/1",
                        _config.FirmsApiKey, west, south, east, north);

                    HttpResponseMessage fireResponse = await Http.GetAsync(firmsUrl);
                    if (fireResponse.IsSuccessStatusCode)
                    {
                        string csvData = await fireResponse.Content.ReadAsStringAsync();
                        string[] lines = csvData.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                        if (lines.Length > 1)
                        {
                            int firesWithin30km = 0;
                            double closestDistance = double.MaxValue;

                            for (int i = 1; i < lines.Length; i++)
                            {
                                string[] cols = lines[i].Split(',');
                                
                                // VIIRS CSV format: lat is col 0, lon is col 1
                                if (cols.Length >= 2 && 
                                    double.TryParse(cols[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fireLat) && 
                                    double.TryParse(cols[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fireLon))
                                {
                                    double dLat = (fireLat - _config.Latitude) * 111.0;
                                    double dLon = (fireLon - _config.Longitude) * (111.0 * Math.Cos(_config.Latitude * Math.PI / 180.0));
                                    double distance = Math.Sqrt((dLat * dLat) + (dLon * dLon));

                                    // Strict 30km filter
                                    if (distance <= radiusKm)
                                    {
                                        firesWithin30km++;
                                        if (distance < closestDistance)
                                            closestDistance = distance;
                                    }
                                }
                            }

                            if (firesWithin30km > 0)
                            {
                                FireText.Text = $"🔥 PROXIMITY THERMAL: {firesWithin30km} fires within 30km (Closest: {closestDistance:F1}km)";
                            }
                            else
                            {
                                FireText.Text = ""; 
                            }
                        }
                        else
                        {
                            FireText.Text = ""; 
                        }
                    }
                }
            }
            catch (Exception ex) { LogError("DisasterLoop - NASA FIRMS API", ex); }

            // 1. EARTHQUAKE DATA (USGS)
            try
            {
                string url = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "https://earthquake.usgs.gov/fdsnws/event/1/query?format=geojson&minmagnitude=4.0&latitude={0:F4}&longitude={1:F4}&maxradiuskm=1000",
                    _config.Latitude, _config.Longitude);
                    
                HttpResponseMessage response = await Http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    string jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);
                    var features = doc.RootElement.GetProperty("features");

                    if (features.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var feature in features.EnumerateArray().Take(3))
                        {
                            var props = feature.GetProperty("properties");
                            long timeInMs = props.GetProperty("time").GetInt64();
                            DateTimeOffset eventTime = DateTimeOffset.FromUnixTimeMilliseconds(timeInMs);
                            
                            if ((DateTimeOffset.UtcNow - eventTime).TotalHours > 12) continue;

                            double mag = props.GetProperty("mag").GetDouble();
                            string place = props.GetProperty("place").GetString() ?? "Unknown Location";
                            int tsunamiFlag = props.GetProperty("tsunami").GetInt32(); 
                            string timeUtc = eventTime.ToUniversalTime().ToString("HH:mm 'UTC'");
                            
                            if (tsunamiFlag == 1)
                                activeAlerts.Add($"🌊 TSUNAMI WARN: M{mag:F1} - {place} [{timeUtc}]");
                            else
                                activeAlerts.Add($"⚠️ SEISMIC ACTV: M{mag:F1} - {place} [{timeUtc}]");
                        }
                    }
                }
            }
            catch (Exception ex) { LogError("DisasterLoop - USGS API", ex); }

            // 2. INFRASTRUCTURE HYDROLOGY MONITOR (IN CUSECS)
            try
            {
                var targetedSites = new (string Name, double Lat, double Lon, double MedFlood, double HighFlood)[]
                {
                    ("Tarbela (KPK)", 34.0883, 72.6980, 250000, 400000),
                    ("Mangla (AJK)", 33.1472, 73.6421, 150000, 225000),
                    ("Warsak (KPK)", 34.1642, 71.3569, 40000, 80000),
                    ("Marala (PB)", 32.6719, 74.4642, 150000, 200000), 
                    ("Rasul (PB)", 32.6828, 73.5283, 150000, 200000),
                    ("Qadirabad (PB)", 32.3217, 73.6872, 200000, 300000),
                    ("Trimmu (PB)", 31.1458, 72.1481, 150000, 200000),
                    ("Taunsa (PB)", 30.5075, 70.8406, 250000, 400000),
                    ("Balloki (PB)", 31.2225, 73.8647, 70000, 100000),
                    ("Sulemanki (PB)", 30.3789, 73.8631, 80000, 120000)
                };

                string lats = string.Join(",", targetedSites.Select(s => s.Lat.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));
                string lons = string.Join(",", targetedSites.Select(s => s.Lon.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));

                string floodUrl = $"https://flood-api.open-meteo.com/v1/flood?latitude={lats}&longitude={lons}&daily=river_discharge&forecast_days=1";

                HttpResponseMessage floodResponse = await Http.GetAsync(floodUrl);
                if (floodResponse.IsSuccessStatusCode)
                {
                    string floodJson = await floodResponse.Content.ReadAsStringAsync();
                    using var floodDoc = JsonDocument.Parse(floodJson);

                    IEnumerable<JsonElement> locations = floodDoc.RootElement.ValueKind == JsonValueKind.Array 
                        ? floodDoc.RootElement.EnumerateArray() 
                        : new[] { floodDoc.RootElement };

                    int siteIndex = 0;
                    foreach (var locNode in locations)
                    {
                        if (siteIndex >= targetedSites.Length) break;
                        var site = targetedSites[siteIndex++];

                        if (locNode.TryGetProperty("daily", out var daily))
                        {
                            var dischargeValues = daily.GetProperty("river_discharge").EnumerateArray().ToList();

                            if (dischargeValues.Count > 0)
                            {
                                double? currentDischargeM3 = dischargeValues[0].ValueKind == JsonValueKind.Number ? dischargeValues[0].GetDouble() : null;

                                if (currentDischargeM3.HasValue)
                                {
                                    // Convert m3/s to Cusecs
                                    double currentDischargeCusecs = currentDischargeM3.Value * 35.3147;

                                    if (currentDischargeCusecs >= site.HighFlood)
                                    {
                                        string alertStr = $"🌊 HIGH FLOOD: {site.Name} [{currentDischargeCusecs:N0} cusecs]";
                                        if (!activeAlerts.Contains(alertStr)) activeAlerts.Add(alertStr);
                                    }
                                    else if (currentDischargeCusecs >= site.MedFlood)
                                    {
                                        string alertStr = $"🌊 MED FLOOD: {site.Name} [{currentDischargeCusecs:N0} cusecs]";
                                        if (!activeAlerts.Contains(alertStr)) activeAlerts.Add(alertStr);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { LogError("DisasterLoop - Hydro Monitor API", ex); }

            // 3. TACTICAL ALERT DISPLAY (Cleaned Up)
            if (activeAlerts.Any())
            {
                // Stacks every single active alert vertically, no limits
                DisasterText.Text = string.Join("\n", activeAlerts);
                DisasterText.Foreground = System.Windows.Media.Brushes.IndianRed;
                
                // Display for 60 seconds, then clear it until the next 15-minute loop
                await Task.Delay(TimeSpan.FromSeconds(60));
                DisasterText.Text = "";
            }
            else
            {
                DisasterText.Text = ""; 
            }
        }

        private string _openSkyToken = null;
        private DateTime _tokenExpiry = DateTime.MinValue;

        private async Task<string> GetOpenSkyTokenAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.OpenSkyClientId) || string.IsNullOrWhiteSpace(_config.OpenSkyClientSecret))
                return null;

            if (!string.IsNullOrEmpty(_openSkyToken) && DateTime.Now < _tokenExpiry.AddMinutes(-2))
                return _openSkyToken;

            try
            {
                string authUrl = "https://auth.opensky-network.org/auth/realms/opensky-network/protocol/openid-connect/token";
                var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "grant_type", "client_credentials" },
                    { "client_id", _config.OpenSkyClientId },
                    { "client_secret", _config.OpenSkyClientSecret }
                });

                var response = await Http.PostAsync(authUrl, requestContent);
                if (!response.IsSuccessStatusCode) return null;

                string jsonStr = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonStr);
                
                _openSkyToken = doc.RootElement.GetProperty("access_token").GetString();
                int expiresInSeconds = doc.RootElement.GetProperty("expires_in").GetInt32();
                _tokenExpiry = DateTime.Now.AddSeconds(expiresInSeconds);

                return _openSkyToken;
            }
            catch (Exception ex) 
            { 
                LogError("GetOpenSkyTokenAsync", ex);
                return null; 
            }
        }

        private async Task<string> GetLocationNameAsync(double lat, double lon)
        {
            try
            {
                string url = $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={lat}&longitude={lon}&localityLanguage=en";
                string response = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                
                string city = doc.RootElement.TryGetProperty("city", out var cityProp) ? cityProp.GetString() : "";
                string country = doc.RootElement.TryGetProperty("countryName", out var countryProp) ? countryProp.GetString() : "";
                string locality = doc.RootElement.TryGetProperty("locality", out var locProp) ? locProp.GetString() : "";

                if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(country))
                    return $"{city}, {country}";
                
                if (!string.IsNullOrWhiteSpace(locality) && !string.IsNullOrWhiteSpace(country))
                    return $"{locality}, {country}";
                    
                if (!string.IsNullOrWhiteSpace(country))
                    return country;
                    
                return $"[{lat:F2}, {lon:F2}]";
            }
            catch
            {
                return $"[{lat:F2}, {lon:F2}]";
            }
        }

        // EXPLICIT NAMESPACE FIX: System.Windows.Media.Brush
        private (System.Windows.Media.Brush brush, string tag) GetAircraftCategory(string callsign, string acType, JsonElement plane)
        {
            string call = callsign.ToUpperInvariant();
            
            int dbFlags = 0;
            if (plane.ValueKind == JsonValueKind.Object && plane.TryGetProperty("dbFlags", out var dbProp) && dbProp.ValueKind == JsonValueKind.Number)
            {
                dbFlags = dbProp.GetInt32();
            }
            
            bool isMilitaryDb = (dbFlags & 1) != 0;
            bool isLaddDb = (dbFlags & 2) != 0;

            if (isMilitaryDb || call.StartsWith("PAF") || call.StartsWith("PAK") || call.StartsWith("RCH") || 
                call.StartsWith("FOR") || acType.StartsWith("C130") || acType.StartsWith("F16") || acType.StartsWith("JF17"))
            {
                // EXPLICIT NAMESPACE FIX: System.Windows.Media.Brushes
                return (System.Windows.Media.Brushes.OrangeRed, " [MIL]");
            }

            if (isLaddDb || call == "LADD" || call == "PIA_PRIV" || call.StartsWith("BLOCKED"))
            {
                return (System.Windows.Media.Brushes.Gold, " [PRIV]");
            }

            if (call.StartsWith("PIA") || call.StartsWith("PKI"))
            {
                return (System.Windows.Media.Brushes.DeepSkyBlue, " [PIA]");
            }

            return (System.Windows.Media.Brushes.LimeGreen, "");
        }

        private async Task FetchFlightDataAsync()
        {
            var formattedPlanes = new List<string>();
            var emergencyPlanes = new List<string>();

            // 1. GLOBAL EMERGENCIES (adsb.lol API)
            string[] emergencyCodes = { "7500", "7600", "7700" };
            foreach (string sqCode in emergencyCodes)
            {
                try 
                {
                    string emgUrl = $"https://api.adsb.lol/v2/squawk/{sqCode}";
                    string emgResponse = await Http.GetStringAsync(emgUrl);
                    using var emgDoc = JsonDocument.Parse(emgResponse);
                    
                    if (emgDoc.RootElement.TryGetProperty("ac", out var acArray) && acArray.ValueKind == JsonValueKind.Array)
                    {
                        string alertType = sqCode switch
                        {
                            "7500" => "HIJACK",
                            "7600" => "RADIO FAIL",
                            "7700" => "EMERGENCY",
                            _ => "ALERT"
                        };

                        foreach (var plane in acArray.EnumerateArray())
                        {
                            string callsign = plane.TryGetProperty("flight", out var f) ? f.GetString()?.Trim() ?? "Unk" : "Unk";
                            if (string.IsNullOrWhiteSpace(callsign)) callsign = "Unk";

                            string acType = plane.TryGetProperty("t", out var tProp) && tProp.ValueKind == JsonValueKind.String 
                                ? tProp.GetString()?.Trim() ?? "UnkType" : "UnkType";

                            string location = "Unk Loc";
                            if (plane.TryGetProperty("lat", out var latProp) && latProp.ValueKind == JsonValueKind.Number &&
                                plane.TryGetProperty("lon", out var lonProp) && lonProp.ValueKind == JsonValueKind.Number)
                            {
                                double lat = latProp.GetDouble();
                                double lon = lonProp.GetDouble();
                                location = await GetLocationNameAsync(lat, lon);
                            }

                            emergencyPlanes.Add($"🚨 {alertType} ({sqCode}): {callsign} | {acType} @ {location}");
                        }
                    }
                }
                catch (Exception ex) { LogError($"FlightLoop - Emergency {sqCode} API", ex); }
            }

            EmergencyText.Text = emergencyPlanes.Any() ? string.Join("\n", emergencyPlanes) : "";

            // 2. LOCAL TRAFFIC & VISUALS
            bool dataLoaded = false;
            JsonDocument activeDoc = null;
            string activeSource = "";

            double latToKm = 111.32; 
            double lonToKm = 111.32 * Math.Cos(_config.Latitude * (Math.PI / 180.0));
            double searchRadiusKm = _config.RadarRangeKm; 

            try
            {
                double radiusNm = searchRadiusKm * 0.539957;
                string primaryUrl = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "https://api.airplanes.live/v2/point/{0:F4}/{1:F4}/{2:F1}",
                    _config.Latitude, _config.Longitude, radiusNm);

                HttpResponseMessage response = await Http.GetAsync(primaryUrl);
                if (response.IsSuccessStatusCode)
                {
                    string jsonString = await response.Content.ReadAsStringAsync();
                    activeDoc = JsonDocument.Parse(jsonString);
                    if (activeDoc.RootElement.TryGetProperty("ac", out var acArray) && acArray.ValueKind == JsonValueKind.Array)
                    {
                        dataLoaded = true;
                        activeSource = "airplanes.live";
                    }
                }
            }
            catch (Exception ex) { LogError("FlightLoop - Airplanes.live API", ex); }

            if (!dataLoaded)
            {
                try
                {
                    if (!Http.DefaultRequestHeaders.UserAgent.TryParseAdd("DascHUD/1.0"))
                    {
                        Http.DefaultRequestHeaders.UserAgent.Clear();
                        Http.DefaultRequestHeaders.UserAgent.ParseAdd("DascHUD/1.0");
                    }

                    string token = await GetOpenSkyTokenAsync();
                    Http.DefaultRequestHeaders.Authorization = !string.IsNullOrEmpty(token) 
                        ? new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token) 
                        : null;

                    double deltaLat = searchRadiusKm / latToKm;
                    double deltaLon = searchRadiusKm / lonToKm;

                    string fallbackUrl = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "https://opensky-network.org/api/states/all?lamin={0:F4}&lamax={1:F4}&lomin={2:F4}&lomax={3:F4}",
                        _config.Latitude - deltaLat, _config.Latitude + deltaLat, _config.Longitude - deltaLon, _config.Longitude + deltaLon);
                        
                    HttpResponseMessage response = await Http.GetAsync(fallbackUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        string jsonString = await response.Content.ReadAsStringAsync();
                        activeDoc = JsonDocument.Parse(jsonString);
                        if (activeDoc.RootElement.TryGetProperty("states", out var states))
                        {
                            dataLoaded = true;
                            activeSource = "OpenSky";
                        }
                    }
                }
                catch (Exception ex) { LogError("FlightLoop - OpenSky Fallback API", ex); }
            }

            // 3. RENDER CANVAS & TEXT LIST
            if (!dataLoaded || activeDoc == null)
            {
                FlightText.Text = $"✈️ {_config.CityCode}: NET DOWN";
                
                foreach (var plane in _radarBlips.Values)
                {
                    var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(5));
                    fadeOut.Completed += (s, e) => AirspaceCanvas.Children.Remove(plane);
                    plane.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
                _radarBlips.Clear();
                return;
            }

            using (activeDoc)
            {
                if (AirspaceCanvas.ActualWidth == 0 || AirspaceCanvas.ActualHeight == 0) return;

                var currentCycleCallsigns = new HashSet<string>();
                double screenCenterX = AirspaceCanvas.ActualWidth / 2.0;
                double screenCenterY = AirspaceCanvas.ActualHeight / 2.0;
                double visualCanvasDiameterKm = _config.RadarRangeKm / 2.0; 
                double pixelsPerKm = AirspaceCanvas.ActualWidth / visualCanvasDiameterKm; 
                int textListCount = 0;

                if (activeSource == "airplanes.live")
                {
                    var acArray = activeDoc.RootElement.GetProperty("ac");
                    foreach (var plane in acArray.EnumerateArray())
                    {
                        string callsign = plane.TryGetProperty("flight", out var f) ? f.GetString()?.Trim() ?? "Unk" : "Unk";
                        if (string.IsNullOrWhiteSpace(callsign)) callsign = "Unk";
                        currentCycleCallsigns.Add(callsign);

                        string acType = plane.TryGetProperty("t", out var tProp) && tProp.ValueKind == JsonValueKind.String 
                            ? tProp.GetString()?.Trim() ?? "" : "";
                        
                        double planeLat = plane.TryGetProperty("lat", out var latProp) && latProp.ValueKind == JsonValueKind.Number ? latProp.GetDouble() : 0;
                        double planeLon = plane.TryGetProperty("lon", out var lonProp) && lonProp.ValueKind == JsonValueKind.Number ? lonProp.GetDouble() : 0;
                        if (planeLat == 0 || planeLon == 0) continue;

                        double altFeet = plane.TryGetProperty("alt_baro", out var altProp) && altProp.ValueKind == JsonValueKind.Number ? altProp.GetDouble() : 0;
                        string alt = plane.TryGetProperty("alt_baro", out var altStr) && altStr.ValueKind == JsonValueKind.String && altStr.GetString() == "ground" ? "TAXI/GATE" : $"{(int)altFeet}ft";

                        double baroRate = plane.TryGetProperty("baro_rate", out var rateProp) && rateProp.ValueKind == JsonValueKind.Number ? rateProp.GetDouble() : 0;
                        string climbArrow = baroRate switch
                        {
                            > 250 => " ↑",
                            < -250 => " ↓",
                            _ => ""
                        };

                        double track = plane.TryGetProperty("track", out var trProp) && trProp.ValueKind == JsonValueKind.Number ? trProp.GetDouble() : 0;
                        double gsKnots = plane.TryGetProperty("gs", out var gsProp) && gsProp.ValueKind == JsonValueKind.Number ? gsProp.GetDouble() : 0;
                        double speedKmh = gsKnots * 1.852; 

                        double kmFromCenterX = (planeLon - _config.Longitude) * lonToKm;
                        double kmFromCenterY = (planeLat - _config.Latitude) * latToKm;
                        double distanceKm = Math.Sqrt((kmFromCenterX * kmFromCenterX) + (kmFromCenterY * kmFromCenterY));

                        var (blipBrush, aoiTag) = GetAircraftCategory(callsign, acType, plane);

                        if (textListCount < 15)
                        {
                            string displayType = string.IsNullOrEmpty(acType) ? "" : $" ({acType})";
                            formattedPlanes.Add($"✈ {callsign}{displayType}{aoiTag} - {alt}{climbArrow} | {distanceKm:F0}km");
                            textListCount++;
                        }

                        if (Math.Abs(kmFromCenterX) > (visualCanvasDiameterKm / 2.0) || Math.Abs(kmFromCenterY) > (visualCanvasDiameterKm / 2.0)) continue;

                        double startX = screenCenterX + (kmFromCenterX * pixelsPerKm);
                        double startY = screenCenterY - (kmFromCenterY * pixelsPerKm);

                        double distanceKmIn60Sec = speedKmh * (60.0 / 3600.0);
                        double headingRad = track * (Math.PI / 180.0);
                        double targetKmX = kmFromCenterX + (distanceKmIn60Sec * Math.Sin(headingRad));
                        double targetKmY = kmFromCenterY + (distanceKmIn60Sec * Math.Cos(headingRad));
                        double targetX = screenCenterX + (targetKmX * pixelsPerKm);
                        double targetY = screenCenterY - (targetKmY * pixelsPerKm);

                        if (!_radarBlips.TryGetValue(callsign, out var planeGroup))
                        {
                            // EXPLICIT NAMESPACE FIX: System.Windows.Controls.Orientation
                            planeGroup = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                            var planeIcon = new TextBlock 
                            { 
                                Text = "✈", FontSize = 14, Foreground = blipBrush,
                                // EXPLICIT NAMESPACE FIX: System.Windows.Point
                                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                                RenderTransform = new RotateTransform(track - 45) 
                            };
                            var planeLabel = new TextBlock 
                            { 
                                Text = $" {callsign}{aoiTag}\n {alt}{climbArrow}", 
                                FontSize = 11, Foreground = blipBrush,
                                Margin = new Thickness(2, -2, 0, 0) 
                            };

                            planeGroup.Children.Add(planeIcon);
                            planeGroup.Children.Add(planeLabel);
                            AirspaceCanvas.Children.Add(planeGroup);
                            _radarBlips[callsign] = planeGroup;

                            Canvas.SetLeft(planeGroup, startX);
                            Canvas.SetTop(planeGroup, startY);
                        }
                        else
                        {
                            var icon = (TextBlock)planeGroup.Children[0];
                            var label = (TextBlock)planeGroup.Children[1];
                            
                            icon.Foreground = blipBrush;
                            label.Foreground = blipBrush;
                            label.Text = $" {callsign}{aoiTag}\n {alt}{climbArrow}";
                            icon.RenderTransform = new RotateTransform(track - 45);
                        }

                        var moveX = new DoubleAnimation { To = targetX, Duration = TimeSpan.FromSeconds(60) };
                        var moveY = new DoubleAnimation { To = targetY, Duration = TimeSpan.FromSeconds(60) };
                        Timeline.SetDesiredFrameRate(moveX, 60);
                        Timeline.SetDesiredFrameRate(moveY, 60);
                        planeGroup.BeginAnimation(Canvas.LeftProperty, moveX);
                        planeGroup.BeginAnimation(Canvas.TopProperty, moveY);
                        planeGroup.BeginAnimation(UIElement.OpacityProperty, null);
                        planeGroup.Opacity = 1.0;
                    }
                }
                else if (activeSource == "OpenSky")
                {
                    var states = activeDoc.RootElement.GetProperty("states");
                    foreach (var plane in states.EnumerateArray()) 
                    {
                        string callsign = plane[1].ValueKind == JsonValueKind.String ? plane[1].GetString()?.Trim() ?? "Unk" : "Unk";
                        if (string.IsNullOrWhiteSpace(callsign)) callsign = "Unk";

                        long lastContact = plane[4].ValueKind == JsonValueKind.Number ? plane[4].GetInt64() : 0;
                        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastContact > 60) continue;

                        currentCycleCallsigns.Add(callsign);

                        bool isOnGround = plane[8].ValueKind == JsonValueKind.True;
                        string alt = isOnGround ? "TAXI/GATE" : plane[7].ValueKind == JsonValueKind.Number ? $"{(int)(plane[7].GetDouble() * 3.28084)}ft" : "Gnd";

                        double vertRateMs = plane[11].ValueKind == JsonValueKind.Number ? plane[11].GetDouble() : 0;
                        string climbArrow = vertRateMs switch
                        {
                            > 1.25 => " ↑",
                            < -1.25 => " ↓",
                            _ => ""
                        };

                        double planeLon = plane[5].ValueKind == JsonValueKind.Number ? plane[5].GetDouble() : 0;
                        double planeLat = plane[6].ValueKind == JsonValueKind.Number ? plane[6].GetDouble() : 0;
                        if (planeLon == 0 || planeLat == 0) continue;
                        
                        double heading = plane[10].ValueKind == JsonValueKind.Number ? plane[10].GetDouble() : 0; 
                        double velocityMs = plane[9].ValueKind == JsonValueKind.Number ? plane[9].GetDouble() : 0; 
                        double speedKmh = velocityMs * 3.6;

                        double kmFromCenterX = (planeLon - _config.Longitude) * lonToKm;
                        double kmFromCenterY = (planeLat - _config.Latitude) * latToKm;
                        double distanceKm = Math.Sqrt((kmFromCenterX * kmFromCenterX) + (kmFromCenterY * kmFromCenterY));

                        var (blipBrush, aoiTag) = GetAircraftCategory(callsign, "", default);

                        if (textListCount < 15)
                        {
                            formattedPlanes.Add($"✈ {callsign}{aoiTag} - {alt}{climbArrow} | {distanceKm:F0}km");
                            textListCount++;
                        }

                        if (Math.Abs(kmFromCenterX) > (visualCanvasDiameterKm / 2.0) || Math.Abs(kmFromCenterY) > (visualCanvasDiameterKm / 2.0)) continue;
                        
                        double startX = screenCenterX + (kmFromCenterX * pixelsPerKm);
                        double startY = screenCenterY - (kmFromCenterY * pixelsPerKm);

                        double distanceKmIn60Sec = speedKmh * (60.0 / 3600.0);
                        double headingRad = heading * (Math.PI / 180.0);
                        double targetKmX = kmFromCenterX + (distanceKmIn60Sec * Math.Sin(headingRad));
                        double targetKmY = kmFromCenterY + (distanceKmIn60Sec * Math.Cos(headingRad));
                        double targetX = screenCenterX + (targetKmX * pixelsPerKm);
                        double targetY = screenCenterY - (targetKmY * pixelsPerKm);

                        if (!_radarBlips.TryGetValue(callsign, out var planeGroup))
                        {
                            // EXPLICIT NAMESPACE FIX: System.Windows.Controls.Orientation
                            planeGroup = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                            var planeIcon = new TextBlock 
                            { 
                                Text = "✈", FontSize = 14, Foreground = blipBrush,
                                // EXPLICIT NAMESPACE FIX: System.Windows.Point
                                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                                RenderTransform = new RotateTransform(heading - 45) 
                            };
                            var planeLabel = new TextBlock 
                            { 
                                Text = $" {callsign}{aoiTag}\n {alt}{climbArrow}", 
                                FontSize = 11, Foreground = blipBrush,
                                Margin = new Thickness(2, -2, 0, 0) 
                            };

                            planeGroup.Children.Add(planeIcon);
                            planeGroup.Children.Add(planeLabel);
                            AirspaceCanvas.Children.Add(planeGroup);
                            _radarBlips[callsign] = planeGroup;

                            Canvas.SetLeft(planeGroup, startX);
                            Canvas.SetTop(planeGroup, startY);
                        }
                        else
                        {
                            var icon = (TextBlock)planeGroup.Children[0];
                            var label = (TextBlock)planeGroup.Children[1];
                            
                            icon.Foreground = blipBrush;
                            label.Foreground = blipBrush;
                            label.Text = $" {callsign}{aoiTag}\n {alt}{climbArrow}";
                            icon.RenderTransform = new RotateTransform(heading - 45);
                        }

                        var moveX = new DoubleAnimation { To = targetX, Duration = TimeSpan.FromSeconds(60) };
                        var moveY = new DoubleAnimation { To = targetY, Duration = TimeSpan.FromSeconds(60) };
                        Timeline.SetDesiredFrameRate(moveX, 60);
                        Timeline.SetDesiredFrameRate(moveY, 60);
                        planeGroup.BeginAnimation(Canvas.LeftProperty, moveX);
                        planeGroup.BeginAnimation(Canvas.TopProperty, moveY);
                        planeGroup.BeginAnimation(UIElement.OpacityProperty, null);
                        planeGroup.Opacity = 1.0;
                    }
                }

                var planesLeftAirspace = _radarBlips.Keys.Except(currentCycleCallsigns).ToList();
                foreach (var staleCallsign in planesLeftAirspace)
                {
                    var stalePlane = _radarBlips[staleCallsign];
                    _radarBlips.Remove(staleCallsign); 

                    var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(5));
                    fadeOut.Completed += (s, ev) => AirspaceCanvas.Children.Remove(stalePlane); 
                    stalePlane.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
            }

            FlightText.Text = formattedPlanes.Any() ? string.Join("\n", formattedPlanes) : $"✈️ {_config.CityCode} RADAR: CLEAR";
        }

        private string ParseWeatherCode(int code) => code switch
        {
            0 => "☀️",
            1 or 2 or 3 => "⛅",
            45 or 48 => "🌁",
            >= 51 and <= 67 => "🌦️",
            >= 71 and <= 77 => "🌨️",
            >= 80 and <= 82 => "🌧️",
            >= 95 => "⛈️",
            _ => "🤷"
        };
    }

    public class AppConfig
    {
        [JsonPropertyName("latitude")]
        public double Latitude { get; set; } = 31.5203; 
        
        [JsonPropertyName("longitude")]
        public double Longitude { get; set; } = 74.4105;
        
        [JsonPropertyName("city_code")]
        public string CityCode { get; set; } = "OPLA";

        [JsonPropertyName("radar_range_km")]
        public double RadarRangeKm { get; set; } = 100.0; 

        [JsonPropertyName("display_monitor")]
        public int DisplayMonitor { get; set; } = 1;
        
        [JsonPropertyName("n2yo_api_key")]
        public string N2YoApiKey { get; set; } = "YOUR_N2YO_API_KEY_HERE";
       
        [JsonPropertyName("firms_api_key")]
        public string FirmsApiKey { get; set; } = "YOUR_FIRMS_API_KEY";

        [JsonPropertyName("skylink_rapidapi_key")]
        public string SkylinkApiKey { get; set; } = "YOUR_RAPIDAPI_KEY";

        [JsonPropertyName("notam_icaos")]
        public List<string> NotamIcaos { get; set; } = new List<string> { "OPRR", "OPKR", "VABF", "VIDF" };

        [JsonPropertyName("opensky_client_id")]
        public string OpenSkyClientId { get; set; } = "";

        [JsonPropertyName("opensky_client_secret")]
        public string OpenSkyClientSecret { get; set; } = "";

        [JsonPropertyName("hud_horizontal_alignment")]
        public string HudHorizontalAlignment { get; set; } = "Right"; 

        [JsonPropertyName("hud_vertical_alignment")]
        public string HudVerticalAlignment { get; set; } = "Bottom"; 

        [JsonPropertyName("hud_margin_left")]
        public double HudMarginLeft { get; set; } = 20;

        [JsonPropertyName("hud_margin_top")]
        public double HudMarginTop { get; set; } = 20;

        [JsonPropertyName("hud_margin_right")]
        public double HudMarginRight { get; set; } = 20;

        [JsonPropertyName("hud_margin_bottom")]
        public double HudMarginBottom { get; set; } = 20;
    }

    public class MeteoResponse
    {
        [JsonPropertyName("current_weather")]
        public CurrentWeather CurrentWeather { get; set; }
    }

    public class CurrentWeather
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }
        [JsonPropertyName("weathercode")]
        public int WeatherCode { get; set; }
    }
}