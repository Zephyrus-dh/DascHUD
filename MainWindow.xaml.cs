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
        private Dictionary<string, System.Windows.Controls.StackPanel> _radarBlips = new Dictionary<string, System.Windows.Controls.StackPanel>();
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly DispatcherTimer _clockTimer;
        private AppConfig _config;

        public MainWindow()
        {
            InitializeComponent();
            
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
            HudContainer.Margin = new Thickness(
                _config.HudMarginLeft, 
                _config.HudMarginTop, 
                _config.HudMarginRight, 
                _config.HudMarginBottom);

            if (Enum.TryParse(_config.HudVerticalAlignment, true, out VerticalAlignment vAlign))
            {
                HudContainer.VerticalAlignment = vAlign;
            }

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
            catch
            {
                WeatherText.Text = "WEATHER: Offline";
            }
        }

        private async Task FetchDisasterDataAsync()
        {
            try
            {
                string url = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "https://earthquake.usgs.gov/fdsnws/event/1/query?format=geojson&minmagnitude=4.0&latitude={0:F4}&longitude={1:F4}&maxradiuskm=1000",
                    _config.Latitude, _config.Longitude);
                    
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
                        
                        long timeInMs = props.GetProperty("time").GetInt64();
                        string timeUtc = DateTimeOffset.FromUnixTimeMilliseconds(timeInMs).ToUniversalTime().ToString("HH:mm 'UTC'");
                        
                        if (tsunamiFlag == 1)
                        {
                            activeAlerts.Add($"🌊 TSUNAMI WARN: M{mag:F1} - {place} [{timeUtc}]");
                        }
                        else
                        {
                            activeAlerts.Add($"⚠️ SEISMIC ACTV: M{mag:F1} - {place} [{timeUtc}]");
                        }
                    }
                }

                if (activeAlerts.Any())
                {
                    DisasterText.Text = string.Join("\n", activeAlerts);
                    DisasterText.Foreground = System.Windows.Media.Brushes.IndianRed;
                    await Task.Delay(TimeSpan.FromSeconds(60));
                    DisasterText.Text = "";
                }
                else
                {
                    DisasterText.Text = ""; 
                }
            }
            catch { DisasterText.Text = ""; }
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
                catch { /* Silently continue */ }
            }

            EmergencyText.Text = emergencyPlanes.Any() ? string.Join("\n", emergencyPlanes) : "";

            // 2. LOCAL TRAFFIC & VISUALS (PRIMARY: airplanes.live | FALLBACK: OpenSky)
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
            catch { /* Fall through to OpenSky */ }

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
                catch { /* Both failed */ }
            }

            // 3. RENDER CANVAS & TEXT LIST
            if (!dataLoaded || activeDoc == null)
            {
                FlightText.Text = $"✈️ {_config.CityCode}: NET DOWN";
                return;
            }

            using (activeDoc)
            {
                // Abort if WPF hasn't finished drawing UI
                if (AirspaceCanvas.ActualWidth == 0 || AirspaceCanvas.ActualHeight == 0) return;

                AirspaceCanvas.BeginAnimation(UIElement.OpacityProperty, null);
                AirspaceCanvas.Opacity = 1.0;

                var currentCycleCallsigns = new HashSet<string>();

                double screenCenterX = AirspaceCanvas.ActualWidth / 2.0;
                double screenCenterY = AirspaceCanvas.ActualHeight / 2.0;
                /*double displayRadiusKm = _config.RadarRangeKm; 
                double pixelsPerKm = (AirspaceCanvas.ActualWidth / 2.0) / displayRadiusKm;*/
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

                        string acType = plane.TryGetProperty("t", out var tProp) && tProp.ValueKind == JsonValueKind.String 
                            ? tProp.GetString()?.Trim() ?? "" : "";
                        
                        double planeLat = plane.TryGetProperty("lat", out var latProp) && latProp.ValueKind == JsonValueKind.Number 
                            ? latProp.GetDouble() : 0;
                        double planeLon = plane.TryGetProperty("lon", out var lonProp) && lonProp.ValueKind == JsonValueKind.Number 
                            ? lonProp.GetDouble() : 0;
                        if (planeLat == 0 || planeLon == 0) continue;

                        double altFeet = plane.TryGetProperty("alt_baro", out var altProp) && altProp.ValueKind == JsonValueKind.Number ? altProp.GetDouble() : 0;
                        string alt = plane.TryGetProperty("alt_baro", out var altStr) && altStr.ValueKind == JsonValueKind.String && altStr.GetString() == "ground" ? "TAXI/GATE" : $"{(int)altFeet}ft";

                        double track = plane.TryGetProperty("track", out var trProp) && trProp.ValueKind == JsonValueKind.Number ? trProp.GetDouble() : 0;
                        double gsKnots = plane.TryGetProperty("gs", out var gsProp) && gsProp.ValueKind == JsonValueKind.Number ? gsProp.GetDouble() : 0;
                        double speedKmh = gsKnots * 1.852; 

                        double kmFromCenterX = (planeLon - _config.Longitude) * lonToKm;
                        double kmFromCenterY = (planeLat - _config.Latitude) * latToKm;

                        if (textListCount < 15)
                        {
                            string displayType = string.IsNullOrEmpty(acType) ? "" : $" ({acType})";
                            formattedPlanes.Add($"✈ {callsign}{displayType} - {alt}");
                            textListCount++;
                        }

                        // Only render planes that fall inside the zoomed-in central half
if (Math.Abs(kmFromCenterX) > (visualCanvasDiameterKm / 2.0) || Math.Abs(kmFromCenterY) > (visualCanvasDiameterKm / 2.0)) continue;

                        double startX = screenCenterX + (kmFromCenterX * pixelsPerKm);
                        double startY = screenCenterY - (kmFromCenterY * pixelsPerKm);

                        double distanceKmIn30Sec = speedKmh * (30.0 / 3600.0);
                        double headingRad = track * (Math.PI / 180.0);
                        double targetKmX = kmFromCenterX + (distanceKmIn30Sec * Math.Sin(headingRad));
                        double targetKmY = kmFromCenterY + (distanceKmIn30Sec * Math.Cos(headingRad));

                        double targetX = screenCenterX + (targetKmX * pixelsPerKm);
                        double targetY = screenCenterY - (targetKmY * pixelsPerKm);

                        currentCycleCallsigns.Add(callsign);

                        if (!_radarBlips.TryGetValue(callsign, out var planeGroup))
                        {
                            planeGroup = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                            var planeIcon = new System.Windows.Controls.TextBlock 
                            { 
                                Text = "✈", FontSize = 14, Foreground = System.Windows.Media.Brushes.LimeGreen,
                                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                                RenderTransform = new System.Windows.Media.RotateTransform(track - 45) 
                            };
                            var planeLabel = new System.Windows.Controls.TextBlock 
                            { 
                                Text = $" {callsign} {(!string.IsNullOrEmpty(acType) ? "[" + acType + "]" : "")}\n {alt}", 
                                FontSize = 11, Foreground = System.Windows.Media.Brushes.LimeGreen,
                                Margin = new System.Windows.Thickness(2, -2, 0, 0) 
                            };

                            planeGroup.Children.Add(planeIcon);
                            planeGroup.Children.Add(planeLabel);
                            AirspaceCanvas.Children.Add(planeGroup);
                            _radarBlips[callsign] = planeGroup;
                        }
                        else
                        {
                            var icon = (System.Windows.Controls.TextBlock)planeGroup.Children[0];
                            var label = (System.Windows.Controls.TextBlock)planeGroup.Children[1];
                            
                            label.Text = $" {callsign} {(!string.IsNullOrEmpty(acType) ? "[" + acType + "]" : "")}\n {alt}";
                            icon.RenderTransform = new System.Windows.Media.RotateTransform(track - 45);
                        }

                        Canvas.SetLeft(planeGroup, startX);
                        Canvas.SetTop(planeGroup, startY);

                        var moveX = new DoubleAnimation(startX, targetX, TimeSpan.FromSeconds(30));
                        var moveY = new DoubleAnimation(startY, targetY, TimeSpan.FromSeconds(30));
                        planeGroup.BeginAnimation(Canvas.LeftProperty, moveX);
                        planeGroup.BeginAnimation(Canvas.TopProperty, moveY);

                        var fadeSequence = new DoubleAnimationUsingKeyFrames();
                        fadeSequence.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero))); 
                        fadeSequence.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)))); 
                        fadeSequence.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(25)))); 
                        fadeSequence.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(29)))); 

                        planeGroup.BeginAnimation(UIElement.OpacityProperty, fadeSequence);
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

                        bool isOnGround = plane[8].ValueKind == JsonValueKind.True;
                        string alt = isOnGround ? "TAXI/GATE" : plane[7].ValueKind == JsonValueKind.Number ? $"{(int)(plane[7].GetDouble() * 3.28084)}ft" : "Gnd";

                        double planeLon = plane[5].ValueKind == JsonValueKind.Number ? plane[5].GetDouble() : 0;
                        double planeLat = plane[6].ValueKind == JsonValueKind.Number ? plane[6].GetDouble() : 0;
                        double heading = plane[10].ValueKind == JsonValueKind.Number ? plane[10].GetDouble() : 0; 
                        
                        double velocityMs = plane[9].ValueKind == JsonValueKind.Number ? plane[9].GetDouble() : 0; 
                        double speedKmh = velocityMs * 3.6;

                        if (planeLon == 0 || planeLat == 0) continue;

                        double kmFromCenterX = (planeLon - _config.Longitude) * lonToKm;
                        double kmFromCenterY = (planeLat - _config.Latitude) * latToKm;

                        if (textListCount < 15)
                        {
                            formattedPlanes.Add($"✈ {callsign} - {alt}");
                            textListCount++;
                        }

// Only render planes that fall inside the zoomed-in central half
if (Math.Abs(kmFromCenterX) > (visualCanvasDiameterKm / 2.0) || Math.Abs(kmFromCenterY) > (visualCanvasDiameterKm / 2.0)) continue;
                        double startX = screenCenterX + (kmFromCenterX * pixelsPerKm);
                        double startY = screenCenterY - (kmFromCenterY * pixelsPerKm);

                        double distanceKmIn30Sec = speedKmh * (30.0 / 3600.0);
                        double headingRad = heading * (Math.PI / 180.0);
                        double targetKmX = kmFromCenterX + (distanceKmIn30Sec * Math.Sin(headingRad));
                        double targetKmY = kmFromCenterY + (distanceKmIn30Sec * Math.Cos(headingRad));

                        double targetX = screenCenterX + (targetKmX * pixelsPerKm);
                        double targetY = screenCenterY - (targetKmY * pixelsPerKm);

                        currentCycleCallsigns.Add(callsign);

                        if (!_radarBlips.TryGetValue(callsign, out var planeGroup))
                        {
                            planeGroup = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                            var planeIcon = new System.Windows.Controls.TextBlock 
                            { 
                                Text = "✈", FontSize = 14, Foreground = System.Windows.Media.Brushes.LimeGreen,
                                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                                RenderTransform = new System.Windows.Media.RotateTransform(heading - 45) 
                            };
                            var planeLabel = new System.Windows.Controls.TextBlock 
                            { 
                                Text = $" {callsign}\n {alt}", 
                                FontSize = 11, Foreground = System.Windows.Media.Brushes.LimeGreen,
                                Margin = new System.Windows.Thickness(2, -2, 0, 0) 
                            };

                            planeGroup.Children.Add(planeIcon);
                            planeGroup.Children.Add(planeLabel);
                            AirspaceCanvas.Children.Add(planeGroup);
                            _radarBlips[callsign] = planeGroup;
                        }
                        else
                        {
                            var icon = (System.Windows.Controls.TextBlock)planeGroup.Children[0];
                            var label = (System.Windows.Controls.TextBlock)planeGroup.Children[1];
                            
                            label.Text = $" {callsign}\n {alt}";
                            icon.RenderTransform = new System.Windows.Media.RotateTransform(heading - 45);
                        }

                        Canvas.SetLeft(planeGroup, startX);
                        Canvas.SetTop(planeGroup, startY);

                        var moveX = new DoubleAnimation(startX, targetX, TimeSpan.FromSeconds(30));
                        var moveY = new DoubleAnimation(startY, targetY, TimeSpan.FromSeconds(30));
                        planeGroup.BeginAnimation(Canvas.LeftProperty, moveX);
                        planeGroup.BeginAnimation(Canvas.TopProperty, moveY);

                        var fadeSequence = new DoubleAnimationUsingKeyFrames();
                        fadeSequence.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero))); 
                        fadeSequence.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)))); 
                        fadeSequence.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(25)))); 
                        fadeSequence.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(29)))); 

                        planeGroup.BeginAnimation(UIElement.OpacityProperty, fadeSequence);
                    }
                }

                var planesLeftAirspace = _radarBlips.Keys.Except(currentCycleCallsigns).ToList();
                foreach (var staleCallsign in planesLeftAirspace)
                {
                    AirspaceCanvas.Children.Remove(_radarBlips[staleCallsign]);
                    _radarBlips.Remove(staleCallsign);
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