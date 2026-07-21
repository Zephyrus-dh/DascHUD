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
using System.Globalization;

namespace DascHUD
{
    public partial class MainWindow : Window
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly DispatcherTimer _clockTimer;
        private AppConfig _config;

        public MainWindow()
        {
            InitializeComponent();
            
            LoadConfig();
            PositionWindowOnTargetMonitor();
            ApplyHudLayout(); // Injects the user's preferred layout instantly

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
                catch
                {
                    _config = new AppConfig();
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
            // Apply JSON margins
            HudContainer.Margin = new Thickness(
                _config.HudMarginLeft, 
                _config.HudMarginTop, 
                _config.HudMarginRight, 
                _config.HudMarginBottom);

            // Apply Vertical Alignment
            if (Enum.TryParse(_config.HudVerticalAlignment, true, out VerticalAlignment vAlign))
            {
                HudContainer.VerticalAlignment = vAlign;
            }

            // Apply Horizontal Alignment & Smart Text Justification
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
                
                TimeText.HorizontalAlignment = hAlign;
                DateText.HorizontalAlignment = hAlign;
                WeatherText.HorizontalAlignment = hAlign;
                DisasterText.HorizontalAlignment = hAlign;
                EmergencyText.HorizontalAlignment = hAlign;
                FlightText.HorizontalAlignment = hAlign;
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
            // Launch decoupled async loops
            _ = WeatherLoopAsync();
            _ = FlightLoopAsync();
            _ = DisasterLoopAsync();
        }

        private void UpdateClock()
        {
            DateTime now = DateTime.Now;
            TimeText.Text = now.ToString("HH:mm");
            DateText.Text = now.ToString("dd-MM-yyyy | dddd");
        }

        // ==========================================
        // INDEPENDENT ASYNC LOOPS
        // ==========================================
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
                await Task.Delay(TimeSpan.FromMinutes(1));
            }
        }

        // ==========================================
        // DATA FETCH METHODS
        // ==========================================
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
            catch
            {
                WeatherText.Text = "WEATHER: Offline";
            }
        }

        private async Task FetchDisasterDataAsync()
        {
            try
            {
                // Regional Bounding Box: Central Asia, South Asia, Middle East (Mag 4.5+)
                string url = "https://earthquake.usgs.gov/fdsnws/event/1/query?format=geojson&minmagnitude=4.5&minlatitude=0&maxlatitude=55&minlongitude=25&maxlongitude=100";
                
                HttpResponseMessage response = await Http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return; 

                string jsonString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonString);
                var features = doc.RootElement.GetProperty("features");

                var activeAlerts = new List<string>();

                if (features.ValueKind == JsonValueKind.Array && features.GetArrayLength() > 0)
                {
                    foreach (var feature in features.EnumerateArray().Take(1))
                    {
                        var props = feature.GetProperty("properties");
                        double mag = props.GetProperty("mag").GetDouble();
                        string place = props.GetProperty("place").GetString() ?? "Unknown Location";
                        int tsunamiFlag = props.GetProperty("tsunami").GetInt32(); 

                        if (tsunamiFlag == 1)
                        {
                            activeAlerts.Add($"🌊 TSUNAMI WARN: M{mag:F1} - {place}");
                        }
                        else
                        {
                            activeAlerts.Add($"⚠️ SEISMIC ACTV: M{mag:F1} - {place}");
                        }
                    }
                }

                if (activeAlerts.Any())
                {
                    DisasterText.Text = string.Join("\n", activeAlerts);
                    DisasterText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    DisasterText.Text = "";
                }
                else
                {
                    DisasterText.Text = "🛡️ REGIONAL TECTONIC: STABLE";
                    DisasterText.Foreground = System.Windows.Media.Brushes.LimeGreen;
                }
            }
            catch { /* Keep existing text if offline */ }
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
            catch { return null; }
        }
        private async Task<string> GetLocationNameAsync(double lat, double lon)
{
    try
    {
        // Free, no-key reverse geocoding API
        string url = $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={lat}&longitude={lon}&localityLanguage=en";
        string response = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(response);
        
        string city = doc.RootElement.TryGetProperty("city", out var cityProp) ? cityProp.GetString() : "";
        string country = doc.RootElement.TryGetProperty("countryName", out var countryProp) ? countryProp.GetString() : "";
        string locality = doc.RootElement.TryGetProperty("locality", out var locProp) ? locProp.GetString() : "";

        // Format nicely depending on what data the API finds
        if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(country))
            return $"{city}, {country}";
        
        if (!string.IsNullOrWhiteSpace(locality) && !string.IsNullOrWhiteSpace(country))
            return $"{locality}, {country}";
            
        if (!string.IsNullOrWhiteSpace(country))
            return country;
            
        return $"[{lat:F2}, {lon:F2}]"; // Fallback to coords if over the ocean or unmapped
    }
    catch
    {
        return $"[{lat:F2}, {lon:F2}]"; // Fallback to coords if offline
    }
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
        catch { /* Continue on global API failure */ }
    }

    // 2. LOCAL TRAFFIC & VISUALS (OpenSky API)
    try
    {
        if (!Http.DefaultRequestHeaders.UserAgent.TryParseAdd("DascHUD/1.0"))
        {
            Http.DefaultRequestHeaders.UserAgent.Clear();
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("DascHUD/1.0");
        }

        string token = await GetOpenSkyTokenAsync();
        if (!string.IsNullOrEmpty(token))
        {
            Http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            Http.DefaultRequestHeaders.Authorization = null; 
        }

        double latToKm = 111.32; 
        double lonToKm = 111.32 * Math.Cos(_config.Latitude * (Math.PI / 180.0));

        double deltaLat = ((_config.RadarRangeKm / 2) + 20) / latToKm;
        double deltaLon = ((_config.RadarRangeKm / 2) + 20) / lonToKm;

        double laMin = _config.Latitude - deltaLat;
        double laMax = _config.Latitude + deltaLat;
        double loMin = _config.Longitude - deltaLon;
        double loMax = _config.Longitude + deltaLon;

        string localUrl = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "https://opensky-network.org/api/states/all?lamin={0:F4}&lamax={1:F4}&lomin={2:F4}&lomax={3:F4}",
            laMin, laMax, loMin, loMax);
            
        HttpResponseMessage response = await Http.GetAsync(localUrl);

        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            string errMsg = code == 429 ? "RATE LIMIT" : $"API ERR {code}";
            FlightText.Text = $"✈️ {_config.CityCode}: {errMsg}";
            return; 
        }

        string jsonString = await response.Content.ReadAsStringAsync();
        using var localDoc = JsonDocument.Parse(jsonString);
        var root = localDoc.RootElement;

        var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(500));
        AirspaceCanvas.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        await Task.Delay(500);

        AirspaceCanvas.Children.Clear();

        double screenCenterX = AirspaceCanvas.ActualWidth / 2;
        double screenCenterY = AirspaceCanvas.ActualHeight / 2;

        if (root.TryGetProperty("states", out var states) && states.ValueKind == JsonValueKind.Array)
        {
            double pixelsPerKm = AirspaceCanvas.ActualWidth / _config.RadarRangeKm; 

            foreach (var plane in states.EnumerateArray().Take(10)) 
            {
                string callsign = plane[1].ValueKind == JsonValueKind.String ? plane[1].GetString()?.Trim() ?? "Unk" : "Unk";
                if (string.IsNullOrWhiteSpace(callsign)) callsign = "Unk";

                bool isOnGround = plane[8].ValueKind == JsonValueKind.True;
                string alt = isOnGround ? "TAXI/GATE" : 
                             plane[7].ValueKind == JsonValueKind.Number ? $"{(int)(plane[7].GetDouble() * 3.28084)}ft" : "Gnd";

                double verticalRate = plane[11].ValueKind == JsonValueKind.Number ? plane[11].GetDouble() : 0;
                string trend = verticalRate > 0.5 ? "↑" : verticalRate < -0.5 ? "↓" : "";

                formattedPlanes.Add($"✈ {callsign} - {alt}");

                double planeLon = plane[5].ValueKind == JsonValueKind.Number ? plane[5].GetDouble() : 0;
                double planeLat = plane[6].ValueKind == JsonValueKind.Number ? plane[6].GetDouble() : 0;
                double velocityMs = plane[9].ValueKind == JsonValueKind.Number ? plane[9].GetDouble() : 0; 
                double heading = plane[10].ValueKind == JsonValueKind.Number ? plane[10].GetDouble() : 0; 

                if (planeLon == 0 || planeLat == 0) continue;

                double kmFromCenterX = (planeLon - _config.Longitude) * lonToKm;
                double kmFromCenterY = (planeLat - _config.Latitude) * latToKm;

                double startX = screenCenterX + (kmFromCenterX * pixelsPerKm);
                double startY = screenCenterY - (kmFromCenterY * pixelsPerKm);

                double velocityKmPerMin = (velocityMs * 60) / 1000.0;
                double rad = heading * (Math.PI / 180.0);
                double deltaXKm = velocityKmPerMin * Math.Sin(rad);
                double deltaYKm = velocityKmPerMin * Math.Cos(rad);

                double endX = startX + (deltaXKm * pixelsPerKm);
                double endY = startY - (deltaYKm * pixelsPerKm); 

                var planeGroup = new System.Windows.Controls.StackPanel 
                { 
                    Orientation = System.Windows.Controls.Orientation.Horizontal 
                };
                
                var planeIcon = new TextBlock 
                { 
                    Text = "✈", 
                    FontSize = 14, 
                    Foreground = System.Windows.Media.Brushes.LimeGreen,
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(heading - 45) 
                };

                var planeLabel = new TextBlock 
                { 
                    Text = $" {callsign} {trend}\n {alt}", 
                    FontSize = 11, 
                    Foreground = System.Windows.Media.Brushes.LimeGreen,
                    Margin = new System.Windows.Thickness(2, -2, 0, 0) 
                };

                planeGroup.Children.Add(planeIcon);
                planeGroup.Children.Add(planeLabel);

                AirspaceCanvas.Children.Add(planeGroup);

                var xAnim = new DoubleAnimation(startX, endX, TimeSpan.FromMinutes(1));
                var yAnim = new DoubleAnimation(startY, endY, TimeSpan.FromMinutes(1));

                planeGroup.BeginAnimation(Canvas.LeftProperty, xAnim);
                planeGroup.BeginAnimation(Canvas.TopProperty, yAnim);
            }
        }

        var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(500));
        AirspaceCanvas.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }
    catch (HttpRequestException)
    {
        FlightText.Text = $"✈️ {_config.CityCode}: NET DOWN";
    }
    catch (Exception)
    {
        FlightText.Text = $"✈️ {_config.CityCode}: APP ERR";
    }

    // 3. UI SYNC 
    FlightText.Text = formattedPlanes.Any() ? string.Join("\n", formattedPlanes) : $"✈️ {_config.CityCode} RADAR: CLEAR";
    EmergencyText.Text = emergencyPlanes.Any() ? string.Join("\n", emergencyPlanes) : "";     
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

    // --- Core Structured JSON Configurations ---
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