# DacsHUD

<p align="center">
  <img src="demo.gif" alt="DachsHUD Demo" width="900">
</p>

> **Monitor the situation while you monitor the situation.**

DacsHUD is a lightweight, transparent desktop Heads-Up Display (HUD) for Windows that provides **ambient situational awareness** while you work.

Instead of opening multiple websites or dashboards, DacsHUD quietly overlays live information on your desktop so you can stay aware of what's happening around you without interrupting your workflow.

---

## Why?

It started as a simple clock.

I use my second monitor for designing, watching videos and working, and I kept losing track of time because I didn't want the Windows taskbar occupying valuable screen space.

So I built a transparent clock.

Then I thought...

*"Since it's already there, why not show the weather?"*

That quickly became aircraft flying overhead.

Then emergency squawks.

Then disaster monitoring.

Before I knew it, my little clock had evolved into an ambient situational awareness HUD.

---

## Features

- 🕒 Live Time, Date & Day
- 🌤️ Weather (Emoji + Temperature)
- ✈️ Live aircraft overlay
- 🛫 Smooth aircraft animations using heading, speed and altitude
- 📝 Upright aircraft labels for improved readability
- 🚨 Emergency Squawk Monitoring (7500 / 7600 / 7700)
- 🌍 Disaster Monitoring
- 🖥️ Multi-monitor support
- ⚙️ Configurable through `config.json`
- 🖱️ Lightweight click-through transparent overlay

---

## Philosophy

DachsHUD isn't trying to replace FlightRadar24, weather websites or disaster dashboards.

Its goal is simple:

> Surface interesting information without demanding your attention.

Think of it as ambient information rather than an interactive dashboard.

---

## Screenshots

> *(Coming Soon)*

---

## Installation

1. Download the latest release.
2. Extract the files.
3. Edit `config.json`.
4. Add your API credentials.
5. Launch `DachsHUD.exe`.

---

## Configuration

Configuration is currently handled through `config.json`.

Available options include:

- User location
- Monitor selection
- HUD positioning
- API credentials
- Polling intervals
- Display preferences

A graphical configurator may be added in a future release if there's enough interest.

---

## Roadmap

- [ ] Area of Interest (AOI) monitoring
- [ ] Interesting Aircraft alerts (Military / Government / SAR / etc.)
- [ ] Linux version
- [ ] Additional situational awareness providers

---

## Built With

- C#
- WPF
- .NET
- Airplanes.live (Primary ADSB Source)
- OpenSky Network (Fallback)
- ADSB.lol
- Open-Meteo
- USGS (Siesmic Activity Monitor)

---

## Contributing

Bug reports, feature requests and pull requests are always welcome.

If you have an idea that fits the philosophy of **ambient situational awareness**, feel free to open an Issue.

---

## License

MIT License

---

## Acknowledgements

DachsHUD uses the following open-source services and resources:

- Airplanes.live
- OpenSky Network
- ADSB.lol
- Open-Meteo
- USGS

---

> **Nothing serious.**
>
> **Just monitoring the situation.**
