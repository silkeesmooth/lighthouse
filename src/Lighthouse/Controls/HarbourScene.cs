using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Lighthouse.Core.Rendering;
using D = System.Drawing;

namespace Lighthouse.Controls;

public sealed record SceneBoat(string Key, string Label, double Speed);

public enum SignalPhase { Ping, Download, Upload, Done }

/// <summary>
/// A speed test in the scene: a beacon lights up on the far shore and light passes between it and the lamp.
/// <see cref="Mbps"/> sets how much light flows and how fast; <see cref="Tag"/> is the name tag over the beacon.
/// </summary>
public sealed record SceneSignal(SignalPhase Phase, double? Mbps, string Tag);

/// <summary>Trouble over the harbour: a storm while a warning is up, lightning when a critical one starts, fog as memory fills (0 to 1).</summary>
public sealed record SceneWeather(bool Storm, bool Lightning, double Fog);

/// <summary>What the scene shows, built once a second from the snapshot.</summary>
public sealed record SceneState(
    double CpuLoad,
    Heat CpuHeat,
    double GpuLoad,
    double RamFraction,
    IReadOnlyList<SceneBoat> Boats,
    string? Ship,
    bool Moon,
    bool MoonThinking,
    SceneSignal? Signal = null,
    SceneWeather? Weather = null,
    double NetDownMbps = 0,
    double NetUpMbps = 0,
    double DiskActivity = 0);

/// <summary>
/// The living lighthouse at the top of the flyout. The beam turns faster with CPU load and warms with CPU heat,
/// the sea rises with RAM and gets choppier with GPU load, the sky follows the clock, job runs sail past
/// as boats, a game is a ship, the local LLM is the moon, glowing while it thinks, and a speed test is light
/// passing between the lamp and a beacon on the far shore. It animates only
/// while visible, at <see cref="FramesPerSecond"/>, and holds still when Windows animations are off.
/// </summary>
public sealed class HarbourScene : FrameworkElement
{
    private SceneState? _target;
    private double _cpu, _gpu, _ram, _beam, _t;
    private double _signalFade, _phaseT; // the far beacon fades in and out; time since the test's phase began
    private double _storm, _fog, _haze, _foam, _flash, _gullsIn, _gullsOut; // eased 0..1 (flash decays)
    private bool _wasLightning;
    private double _gullT; // the gulls' own clock: it stops while a speed test shows its lights
    private SceneSignal? _signal; // kept while the beacon fades out after the test
    private SignalPhase? _phase;
    private readonly Dictionary<string, double> _boatX = [];
    private TimeSpan _lastFrame;
    private bool _running;

    /// <summary>The height the scene is drawn for. Shorter scenes (the mini monitor) are the same picture scaled
    /// down evenly and widened, so the lighthouse never squashes.</summary>
    private const double DesignHeight = 150;

    /// <summary>A fixed time of day (previews); null means now.</summary>
    public DateTime? Clock { get; set; }

    /// <summary>30 in the flyout; the always-on mini monitor uses less.</summary>
    public double FramesPerSecond { get; set; } = 30;

    /// <summary>Name tags over boats and ships; too small to read in the mini monitor.</summary>
    public bool ShowLabels { get; set; } = true;

    private double Scale => ActualHeight > 0 ? Math.Min(1, ActualHeight / DesignHeight) : 1;

    public HarbourScene()
    {
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) StartAnimation();
            else StopAnimation();
        };
    }

    /// <summary>False holds the picture still (it still redraws once a second with new numbers).</summary>
    public bool AnimationEnabled { get; set; } = true;

    private bool Animated => AnimationEnabled && SystemParameters.ClientAreaAnimation;

    public void Update(SceneState state)
    {
        bool first = _target is null;
        _target = state;
        if (first)
        {
            _cpu = state.CpuLoad;
            _gpu = state.GpuLoad;
            _ram = state.RamFraction;
        }
        foreach (var boat in state.Boats.Where(b => !_boatX.ContainsKey(b.Key)))
            _boatX[boat.Key] = 0.3 + 0.55 * (_boatX.Count * 0.37 % 1); // spread new boats out
        foreach (string gone in _boatX.Keys.Except(state.Boats.Select(b => b.Key)).ToList()) _boatX.Remove(gone);
        if (!_running) Step(Animated ? 0 : 1);
        InvalidateVisual();
    }

    // A timer at the scene's own frame rate. CompositionTarget.Rendering looked natural but, while subscribed,
    // makes WPF render every display frame (180 Hz on the main monitor): measured 2026-10-22 at ~0.7% of the
    // machine for the mini monitor alone.
    private DispatcherTimer? _timer;
    private readonly Stopwatch _clock = new();

    private void StartAnimation()
    {
        if (_running || !Animated) return;
        _running = true;
        _lastFrame = _clock.Elapsed;
        _clock.Start();
        _timer ??= new DispatcherTimer(DispatcherPriority.Render);
        _timer.Interval = TimeSpan.FromSeconds(1 / FramesPerSecond);
        _timer.Tick += OnFrame;
        _timer.Start();
    }

    private void StopAnimation()
    {
        if (!_running) return;
        _running = false;
        _timer!.Stop();
        _timer.Tick -= OnFrame;
        _clock.Stop();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        double dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        Step(Math.Min(dt, 0.25));
        InvalidateVisual();
    }

    /// <summary>Moves time on: eases toward the latest numbers, turns the beam, sails the boats.</summary>
    public void Step(double dt)
    {
        if (_target is not { } s) return;
        double k = dt <= 0 ? 0 : 1 - Math.Exp(-dt * 2.5);
        _cpu += (s.CpuLoad - _cpu) * k;
        _gpu += (s.GpuLoad - _gpu) * k;
        _ram += (s.RamFraction - _ram) * k;
        _t += dt;
        _beam += SceneMath.BeamSpeed(_cpu) * dt;
        if (s.Signal is { } signal)
        {
            if (signal.Phase != _phase) _phaseT = 0;
            _phase = signal.Phase;
            _signal = signal;
        }
        else
        {
            _phase = null;
        }
        _phaseT += dt;
        // Weather and wildlife ease toward their targets over a few seconds; lightning flashes once as it starts.
        double ease = dt <= 0 ? 1 : 1 - Math.Exp(-dt * 1.2);
        var weather = s.Weather ?? new SceneWeather(false, false, 0);
        _storm += ((weather.Storm || weather.Lightning ? 1 : 0) - _storm) * ease;
        _fog += (Math.Clamp(weather.Fog, 0, 1) - _fog) * ease;
        _haze += ((s.CpuHeat == Heat.Hot ? 1 : 0) - _haze) * ease;
        _foam += (Math.Clamp(s.DiskActivity, 0, 1) - _foam) * ease;
        if (weather.Lightning && !_wasLightning) _flash = 1;
        _wasLightning = weather.Lightning;
        _flash = Math.Max(0, _flash - dt * 2.5);
        bool resting = s.Signal is not null; // the speed test's own light has the sky
        _gullsIn += ((resting || s.NetDownMbps < 0.5 ? 0 : 1) - _gullsIn) * ease;
        _gullsOut += ((resting || s.NetUpMbps < 0.5 ? 0 : 1) - _gullsOut) * ease;
        if (!resting) _gullT += dt;

        double fadeTo = s.Signal is null ? 0 : 1;
        _signalFade = dt <= 0 ? fadeTo : _signalFade + (fadeTo - _signalFade) * (1 - Math.Exp(-dt * 3));
        if (_signalFade < 0.01 && s.Signal is null) _signal = null;
        double width = Math.Max(ActualWidth / Scale, 300);
        foreach (var boat in s.Boats)
        {
            double x = _boatX[boat.Key] + boat.Speed * dt / width;
            _boatX[boat.Key] = x > 1.08 ? 0.16 : x; // sail off the right edge, return from behind the rock
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 20 || ActualHeight < 20 || _target is not { } s) return;
        double scale = Scale;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 12 * Math.Max(scale, 0.75), 12 * Math.Max(scale, 0.75)));
        dc.PushTransform(new ScaleTransform(scale, scale));
        double w = ActualWidth / scale, h = ActualHeight / scale;
        var bounds = new Rect(0, 0, w, h);

        DateTime now = Clock ?? DateTime.Now;
        double hour = now.Hour + now.Minute / 60.0;
        double night = SceneMath.Night(hour);
        var (top, bottom) = SceneMath.SkyAt(hour);
        dc.DrawRectangle(new LinearGradientBrush(C(top), C(bottom), 90), null, bounds);

        DrawStars(dc, w, h, night * (1 - _storm));
        if (s.Moon) DrawMoon(dc, w * 0.85, h * 0.24, night, s.MoonThinking);
        DrawStorm(dc, w, h, night);

        double sea = SceneMath.SeaLevel(_ram) * h;
        double amp = SceneMath.WaveHeight(_gpu);
        var seaFront = C(SceneMath.Mix(Hex("#2e6171"), Hex("#10232a"), night));
        var seaBack = C(SceneMath.Mix(Hex("#5b8a97"), Hex("#1d3a44"), night));

        var labels = new List<(string Text, double X, double Y)>();
        dc.DrawGeometry(new SolidColorBrush(seaBack), null, Waves(w, h, sea - 6, amp * 0.7, 1.7));
        Point? farLamp = _signal is { } shown ? DrawFarBeacon(dc, w, sea, amp, night, shown, labels) : null;
        foreach (var boat in s.Boats) labels.Add(DrawBoat(dc, _boatX[boat.Key] * w, sea, amp, boat.Label, night));
        if (s.Ship is { } ship) labels.Add(DrawShip(dc, w * 0.64, sea, amp, ship, night));
        dc.DrawGeometry(new SolidColorBrush(seaFront), null, Waves(w, h, sea, amp, 0));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(50 + 40 * night), 237, 243, 245)), 1),
            Crest(w, sea, amp));
        DrawFoam(dc, w, sea, amp, night);

        DrawLighthouse(dc, w, h, night, s.CpuHeat);
        DrawHaze(dc, w, h);
        DrawGulls(dc, w, h, s, night);
        if (_signal is { } signal && farLamp is { } far) DrawSignal(dc, new Point(w * 0.15, h * 0.30 - 10), far, signal);
        DrawFog(dc, w, h, sea, night);
        if (_flash > 0.01) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(170 * _flash), 255, 255, 255)), null, bounds);
        if (ShowLabels) DrawLabels(dc, labels, night, w);
        dc.Pop(); // scale
        dc.Pop(); // clip
    }

    private double WaveY(double x, double baseY, double amp, double phase) =>
        baseY + amp * (0.6 * Math.Sin(x * 0.045 + _t * 1.2 + phase) + 0.4 * Math.Sin(x * 0.11 - _t * 1.7 + phase * 2));

    private StreamGeometry Waves(double w, double h, double baseY, double amp, double phase)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (double x = 0; x <= w + 4; x += 4) ctx.LineTo(new Point(x, WaveY(x, baseY, amp, phase)), true, false);
            ctx.LineTo(new Point(w, h), true, false);
        }
        g.Freeze();
        return g;
    }

    private StreamGeometry Crest(double w, double baseY, double amp)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, WaveY(0, baseY, amp, 0)), false, false);
            for (double x = 4; x <= w + 4; x += 4) ctx.LineTo(new Point(x, WaveY(x, baseY, amp, 0)), true, true);
        }
        g.Freeze();
        return g;
    }

    private void DrawStars(DrawingContext dc, double w, double h, double night)
    {
        if (night < 0.02) return;
        for (int i = 0; i < 46; i++)
        {
            double twinkle = 0.5 + 0.5 * Math.Sin(_t * 1.4 + i * 1.7);
            byte alpha = (byte)(255 * night * (0.3 + 0.7 * twinkle));
            double size = 0.6 + Hash(i + 300) * 0.9;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, 237, 243, 245)), null,
                new Point(Hash(i) * w, Hash(i + 100) * h * 0.55), size, size);
        }
    }

    private void DrawMoon(DrawingContext dc, double x, double y, double night, bool thinking)
    {
        double glowRadius = thinking ? 24 + 5 * Math.Sin(_t * 3) : 15;
        byte glowAlpha = (byte)(thinking ? 150 : 40 + 50 * night);
        var glow = new RadialGradientBrush(Color.FromArgb(glowAlpha, 241, 227, 143), Color.FromArgb(0, 241, 227, 143));
        dc.DrawEllipse(glow, null, new Point(x, y), glowRadius, glowRadius);
        var disk = new EllipseGeometry(new Point(x, y), 8.5, 8.5);
        var bite = new EllipseGeometry(new Point(x + 4.5, y - 3), 7.5, 7.5);
        var crescent = new CombinedGeometry(GeometryCombineMode.Exclude, disk, bite);
        byte moonAlpha = (byte)(170 + 85 * Math.Max(night, thinking ? 1 : 0));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(moonAlpha, 250, 246, 226)), null, crescent);
    }

    private void DrawLighthouse(DrawingContext dc, double w, double h, double night, Heat heat)
    {
        double cx = w * 0.15, rockTop = h * 0.66, towerTop = h * 0.30;
        Color Dim(string hex) => C(SceneMath.Mix(Hex(hex), Hex("#1d2c31"), 0.45 * night));

        dc.DrawGeometry(new SolidColorBrush(Dim("#4a5d63")), null, Polygon(
            (cx - 42, h), (cx - 35, rockTop + 10), (cx - 22, rockTop + 1), (cx - 6, rockTop - 2), (cx + 15, rockTop + 1),
            (cx + 28, rockTop + 9), (cx + 40, h)));

        // Tower: a tapered column in four red and white bands.
        double baseY = rockTop + 3, bottomHalf = 13, topHalf = 8.5;
        for (int band = 0; band < 4; band++)
        {
            double y0 = baseY - (baseY - towerTop) * band / 4, y1 = baseY - (baseY - towerTop) * (band + 1) / 4;
            double half0 = bottomHalf - (bottomHalf - topHalf) * band / 4, half1 = bottomHalf - (bottomHalf - topHalf) * (band + 1) / 4;
            dc.DrawGeometry(new SolidColorBrush(Dim(band % 2 == 0 ? "#edf3f5" : "#e26d5c")), null,
                Polygon((cx - half0, y0), (cx + half0, y0), (cx + half1, y1), (cx - half1, y1)));
        }
        var dark = new SolidColorBrush(Dim("#1d2c31"));
        dc.DrawRectangle(dark, null, new Rect(cx - 3, baseY - 9, 6, 9)); // door
        dc.DrawRectangle(dark, null, new Rect(cx - 12, towerTop - 3, 24, 3)); // gallery

        Color beamColor = BeamColor(heat);
        double lampY = towerTop - 10;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, beamColor.R, beamColor.G, beamColor.B)), new Pen(dark, 1.2),
            new Rect(cx - 7, towerTop - 15, 14, 12));
        dc.DrawGeometry(new SolidColorBrush(Dim("#234c59")), null, Polygon((cx - 9.5, towerTop - 15), (cx, towerTop - 24), (cx + 9.5, towerTop - 15)));
        dc.DrawEllipse(dark, null, new Point(cx, towerTop - 25), 1.6, 1.6);

        // The beam turns around the tower: long and narrow side-on, short and wide as it swings past.
        double c = Math.Cos(_beam), facing = Math.Max(0, Math.Sin(_beam));
        double strength = Math.Min(1, (0.45 + 0.55 * night) * (heat == Heat.Hot ? 1.5 : heat == Heat.Warm ? 1.2 : 1));
        if (Math.Abs(c) > 0.04)
        {
            double far = cx + c * w * 1.15, spread = 9 + 16 * (1 - Math.Abs(c));
            byte alpha = (byte)(255 * (0.16 + 0.5 * Math.Abs(c)) * strength);
            dc.DrawGeometry(BeamBrush(beamColor, alpha, cx, far, lampY), null,
                Polygon((cx, lampY - 2), (far, lampY - spread), (far, lampY + spread), (cx, lampY + 2)));
            // A brighter core, a third as wide, so the beam's colour still reads against a daytime sky.
            double core = spread * 0.33;
            dc.DrawGeometry(BeamBrush(beamColor, (byte)Math.Min(255, alpha * 1.6), cx, cx + (far - cx) * 0.8, lampY), null,
                Polygon((cx, lampY - 1), (cx + (far - cx) * 0.8, lampY - core), (cx + (far - cx) * 0.8, lampY + core), (cx, lampY + 1)));
        }
        double flare = 12 + 14 * facing;
        var glow = new RadialGradientBrush(Color.FromArgb((byte)(170 * strength + 85 * facing), beamColor.R, beamColor.G, beamColor.B),
            Color.FromArgb(0, beamColor.R, beamColor.G, beamColor.B));
        dc.DrawEllipse(glow, null, new Point(cx, lampY), flare, flare);
    }

    /// <summary>A darker sky, clouds drifting along the top and slanting rain, all as strong as the storm is.</summary>
    private void DrawStorm(DrawingContext dc, double w, double h, double night)
    {
        if (_storm < 0.02) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(110 * _storm), 23, 35, 39)), null, new Rect(0, 0, w, h));
        var cloud = new SolidColorBrush(Color.FromArgb((byte)(200 * _storm), (byte)(74 - 30 * night), (byte)(93 - 35 * night), (byte)(99 - 35 * night)));
        for (int i = 0; i < 7; i++)
        {
            double x = ((Hash(i + 40) * w * 1.4 + _t * (4 + 3 * Hash(i + 50))) % (w * 1.4)) - w * 0.2;
            double y = 6 + Hash(i + 60) * 18, rx = 28 + 22 * Hash(i + 70), ry = 9 + 6 * Hash(i + 80);
            dc.DrawEllipse(cloud, null, new Point(x, y), rx, ry);
            dc.DrawEllipse(cloud, null, new Point(x + rx * 0.6, y + 4), rx * 0.7, ry * 0.8);
        }
        var rain = new Pen(new SolidColorBrush(Color.FromArgb((byte)(120 * _storm), 207, 224, 230)), 1);
        for (int i = 0; i < 70; i++)
        {
            double x = (Hash(i + 200) * (w + 40) + _t * 30) % (w + 40) - 20;
            double y = (Hash(i + 300) * h + _t * (160 + 60 * Hash(i + 400))) % h;
            dc.DrawLine(rain, new Point(x, y), new Point(x - 3, y + 8));
        }
    }

    /// <summary>Fog rolls in as memory fills: drifting bands over the sea and a pale veil over everything.</summary>
    private void DrawFog(DrawingContext dc, double w, double h, double sea, double night)
    {
        if (_fog < 0.02) return;
        byte shade = (byte)(236 - 120 * night);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(90 * _fog), shade, shade, shade)), null, new Rect(0, 0, w, h));
        for (int i = 0; i < 5; i++)
        {
            double y = sea - 34 + i * 12, drift = (_t * (6 + 2 * i) + Hash(i + 90) * w) % (w * 1.6) - w * 0.3;
            var clear = Color.FromArgb(0, shade, shade, shade);
            var band = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop(clear, 0),
                    new GradientStop(Color.FromArgb((byte)(130 * _fog), shade, shade, shade), 0.5),
                    new GradientStop(clear, 1),
                },
            };
            dc.DrawRoundedRectangle(band, null, new Rect(drift, y, w * 0.8, 10), 5, 5);
        }
    }

    /// <summary>Heat shimmer over the lamp while the CPU runs hot: thin warm strokes that waver as they rise.</summary>
    private void DrawHaze(DrawingContext dc, double w, double h)
    {
        if (_haze < 0.02) return;
        // Beside the lantern and short, so it stays inside the scene's top edge.
        double cx = w * 0.15 + 14, top = h * 0.30 - 4;
        for (int i = 0; i < 5; i++)
        {
            double q = (_t * 0.6 + i / 5.0) % 1; // rises and fades
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                double x0 = cx + i * 3.5;
                ctx.BeginFigure(new Point(x0, top - q * 10), false, false);
                for (int k = 1; k <= 6; k++)
                    ctx.LineTo(new Point(x0 + 1.8 * Math.Sin(_t * 5 + k + i), top - q * 10 - k * 2.2), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(120 * _haze * (1 - q)), 241, 227, 143)), 1.1), g);
        }
    }

    /// <summary>
    /// Gulls: in from the sea while the PC downloads, out to sea while it uploads, more and faster for a faster
    /// line (<see cref="SceneMath.SignalFlow"/>). Each is a flapping "v".
    /// </summary>
    private void DrawGulls(DrawingContext dc, double w, double h, SceneState s, double night)
    {
        var ink = Color.FromArgb(255, (byte)(29 + 200 * night), (byte)(44 + 190 * night), (byte)(49 + 190 * night));
        void Flock(double strength, double mbps, bool inbound, int seed)
        {
            if (strength < 0.02) return;
            var (count, rate) = SceneMath.SignalFlow(mbps);
            int birds = Math.Clamp((count - 1) / 2, 1, 5);
            for (int i = 0; i < birds; i++)
            {
                double q = (_gullT * rate * 0.12 + i / (double)birds + Hash(seed + i) * 0.1) % 1;
                double x = inbound ? w * (1.05 - 0.8 * q) : w * (0.25 + 0.8 * q);
                double y = h * (0.14 + 0.16 * Hash(seed + i + 20)) + 4 * Math.Sin(q * 9 + i);
                double flap = 2.4 * Math.Sin(_gullT * (9 + i) + i), span = 4.2;
                double dir = inbound ? -1 : 1;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(x - span * dir, y - flap), false, false);
                    ctx.QuadraticBezierTo(new Point(x - span * 0.4 * dir, y - 1.2), new Point(x, y + 0.6), true, true);
                    ctx.QuadraticBezierTo(new Point(x + span * 0.4 * dir, y - 1.2), new Point(x + span * dir, y - flap), true, true);
                }
                g.Freeze();
                double edge = Math.Sin(Math.PI * q); // fade in and out at the ends of the flight
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(230 * strength * edge), ink.R, ink.G, ink.B)), 1.3)
                    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
            }
        }
        Flock(_gullsIn, s.NetDownMbps, inbound: true, seed: 500);
        Flock(_gullsOut, s.NetUpMbps, inbound: false, seed: 600);
    }

    /// <summary>Foam on the wave crests, more of it the busier the disk.</summary>
    private void DrawFoam(DrawingContext dc, double w, double sea, double amp, double night)
    {
        if (_foam < 0.03) return;
        var foam = new SolidColorBrush(Color.FromArgb((byte)(200 - 60 * night), 245, 250, 251));
        int flecks = (int)(6 + 34 * _foam);
        for (int i = 0; i < flecks; i++)
        {
            double x = (Hash(i + 700) * w + _t * 8) % w;
            double flicker = 0.5 + 0.5 * Math.Sin(_t * 3 + i * 2.3);
            if (flicker < 0.35) continue;
            double y = WaveY(x, sea, amp, 0) - 0.8;
            dc.DrawEllipse(foam, null, new Point(x, y), 1.6 * flicker, 0.9);
        }
    }

    /// <summary>A slim beacon on the far shore, on the back waves; its lamp blinks while the test runs. Returns the lamp.</summary>
    private Point DrawFarBeacon(DrawingContext dc, double w, double sea, double amp, double night, SceneSignal signal,
        List<(string Text, double X, double Y)> labels)
    {
        double x = w * 0.88, baseY = WaveY(x, sea - 6, amp * 0.7, 1.7) + 2, top = baseY - 17;
        byte a = (byte)(255 * _signalFade);
        var body = new SolidColorBrush(Color.FromArgb(a, 29, 44, 49));
        dc.DrawGeometry(body, null, Polygon((x - 3.2, baseY), (x + 3.2, baseY), (x + 2, top), (x - 2, top)));
        dc.DrawRectangle(body, null, new Rect(x - 3.5, top - 1.5, 7, 1.5));
        var lamp = new Point(x, top - 4);
        // Blinks twice a second while testing, steady once the answer is in.
        double lit = signal.Phase == SignalPhase.Done ? 1 : 0.35 + 0.65 * Math.Max(0, Math.Sin(_t * Math.PI * 4));
        var color = Color.FromRgb(241, 227, 143);
        var glow = new RadialGradientBrush(Color.FromArgb((byte)(a * (0.5 + 0.3 * night) * lit), color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B));
        dc.DrawEllipse(glow, null, lamp, 9, 9);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(a * (0.4 + 0.6 * lit)), 255, 250, 228)), null, lamp, 2.2, 2.2);
        if (_signalFade > 0.5) labels.Add((signal.Tag, x, top - 26));
        return lamp;
    }

    /// <summary>
    /// The test itself. Ping: rings leave the lamp. Download: gold light comes in from the far beacon; upload: coral
    /// light goes out to it. Faster connections send more light, faster, along an arc over the sea.
    /// </summary>
    private void DrawSignal(DrawingContext dc, Point lamp, Point far, SceneSignal signal)
    {
        double fade = _signalFade;
        if (fade < 0.02) return;
        if (signal.Phase == SignalPhase.Ping)
        {
            for (int k = 0; k < 2; k++)
            {
                double q = (_phaseT * 0.9 + k / 2.0) % 1;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * (1 - q) * fade), 255, 246, 216)), 1.4);
                dc.DrawEllipse(null, pen, lamp, 6 + 70 * q, 6 + 70 * q);
            }
            return;
        }
        if (signal.Phase == SignalPhase.Done) return;

        bool down = signal.Phase == SignalPhase.Download;
        var color = down ? Color.FromRgb(241, 227, 143) : Color.FromRgb(226, 109, 92); // khaki in, bittersweet out
        var (count, rate) = SceneMath.SignalFlow(signal.Mbps);
        var apex = new Point((lamp.X + far.X) / 2, Math.Max(6, Math.Min(lamp.Y, far.Y) - 24));
        Point At(double p) // a quadratic curve from the lamp (0) to the far beacon (1)
        {
            double u = 1 - p;
            return new Point(u * u * lamp.X + 2 * u * p * apex.X + p * p * far.X, u * u * lamp.Y + 2 * u * p * apex.Y + p * p * far.Y);
        }
        for (int i = 0; i < count; i++)
        {
            double q = (_phaseT * rate + i / (double)count) % 1;
            double edge = Math.Sin(Math.PI * q); // light appears at one end and fades into the other
            for (int trail = 3; trail >= 0; trail--)
            {
                double qt = q - trail * 0.018;
                if (qt < 0) continue;
                var at = At(down ? 1 - qt : qt);
                double alpha = fade * edge * (trail == 0 ? 1 : 0.5 - trail * 0.12);
                if (trail == 0)
                {
                    var glow = new RadialGradientBrush(Color.FromArgb((byte)(150 * alpha), color.R, color.G, color.B),
                        Color.FromArgb(0, color.R, color.G, color.B));
                    dc.DrawEllipse(glow, null, at, 7, 7);
                }
                double r = trail == 0 ? 2.5 : 1.8 - trail * 0.3;
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B)), null, at, r, r);
            }
        }
    }

    private (string, double, double) DrawBoat(DrawingContext dc, double x, double sea, double amp, string label, double night)
    {
        double y = WaveY(x, sea, amp, 0) + 1;
        double slope = Math.Atan2(WaveY(x + 6, sea, amp, 0) - WaveY(x - 6, sea, amp, 0), 12) * 180 / Math.PI;
        dc.PushTransform(new RotateTransform(slope, x, y));
        var hull = new SolidColorBrush(C(SceneMath.Mix(Hex("#1d2c31"), Hex("#0b1519"), night)));
        dc.DrawGeometry(hull, null, Polygon((x - 11, y - 3), (x + 11, y - 3), (x + 7, y + 3), (x - 7, y + 3)));
        dc.DrawLine(new Pen(hull, 1), new Point(x, y - 3), new Point(x, y - 21));
        dc.DrawGeometry(new SolidColorBrush(Hex2("#edf3f5", night)), null, Polygon((x + 1, y - 20), (x + 1, y - 5), (x + 11, y - 5)));
        dc.DrawGeometry(new SolidColorBrush(Hex2("#d9e5d6", night)), null, Polygon((x - 1, y - 17), (x - 1, y - 5), (x - 8, y - 5)));
        dc.Pop();
        return (label, x, y - 36);
    }

    private (string, double, double) DrawShip(DrawingContext dc, double x, double sea, double amp, string name, double night)
    {
        double y = WaveY(x, sea, amp, 0) + 1;
        double roll = Math.Sin(_t * 0.9) * 1.5;
        dc.PushTransform(new RotateTransform(roll, x, y));
        var hull = new SolidColorBrush(C(SceneMath.Mix(Hex("#1d2c31"), Hex("#4a5d63"), night)));
        dc.DrawGeometry(hull, null, Polygon((x - 32, y - 5), (x + 36, y - 5), (x + 29, y + 5), (x - 27, y + 5)));
        dc.DrawRectangle(new SolidColorBrush(Hex2("#c4483a", night)), null, new Rect(x - 28, y + 1.5, 57, 2));
        dc.DrawRectangle(new SolidColorBrush(Hex2("#edf3f5", night)), null, new Rect(x - 16, y - 13, 22, 8));
        dc.DrawRectangle(new SolidColorBrush(Hex2("#2e6171", night)), null, new Rect(x + 10, y - 19, 7, 14));
        dc.DrawRectangle(new SolidColorBrush(Hex2("#f1e38f", night)), null, new Rect(x + 10, y - 19, 7, 2.5));
        for (int i = 0; i < 4; i++) // windows: lit at night
            dc.DrawRectangle(new SolidColorBrush(night > 0.3 ? Color.FromRgb(241, 227, 143) : Color.FromRgb(35, 76, 89)), null,
                new Rect(x - 14 + i * 5, y - 11, 2.5, 2.5));
        for (int i = 0; i < 3; i++) // smoke drifts up and away
        {
            double p = (_t * 0.35 + i / 3.0) % 1;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(110 * (1 - p)), 217, 229, 214)), null,
                new Point(x + 13 - p * 16, y - 22 - p * 14), 2.5 + p * 4, 2.5 + p * 4);
        }
        dc.Pop();
        return (name, x, y - 46);
    }

    /// <summary>Name tags over the boats and the ship, left to right, each lifted above any it would overlap.</summary>
    private void DrawLabels(DrawingContext dc, List<(string Text, double X, double Y)> labels, double night, double width)
    {
        var placed = new List<Rect>();
        var ink = new SolidColorBrush(night > 0.5 ? Color.FromRgb(237, 243, 245) : Color.FromRgb(29, 44, 49));
        var fill = new SolidColorBrush(night > 0.5 ? Color.FromArgb(200, 23, 35, 39) : Color.FromArgb(215, 250, 252, 250));
        foreach (var (text, x, y) in labels.OrderBy(l => l.X))
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 10,
                ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            // Centred on what it names, but never off either edge (the far beacon sits near the right one).
            double left = Math.Clamp(x - ft.Width / 2 - 6, 4, Math.Max(4, width - ft.Width - 16));
            var tag = new Rect(left, y - 1, ft.Width + 12, ft.Height + 2);
            while (placed.Any(r => r.IntersectsWith(tag))) tag.Offset(0, -(tag.Height + 3));
            placed.Add(tag);
            dc.DrawRoundedRectangle(fill, null, tag, 4, 4);
            dc.DrawText(ft, new Point(tag.X + 6, tag.Y + 1));
        }
    }

    private static LinearGradientBrush BeamBrush(Color color, byte alpha, double from, double to, double y) =>
        new(Color.FromArgb(alpha, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B),
            new Point(from, y), new Point(to, y))
        {
            MappingMode = BrushMappingMode.Absolute,
        };

    private static Color BeamColor(Heat heat) => heat switch
    {
        Heat.Hot => Color.FromRgb(222, 72, 52),    // bittersweet, deepened so it reads as red over a blue sky
        Heat.Warm => Color.FromRgb(241, 227, 143), // khaki
        _ => Color.FromRgb(255, 246, 216),         // warm white
    };

    private static StreamGeometry Polygon(params (double X, double Y)[] points)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, points[0].Y), true, true);
            for (int i = 1; i < points.Length; i++) ctx.LineTo(new Point(points[i].X, points[i].Y), true, false);
        }
        g.Freeze();
        return g;
    }

    private static double Hash(int i)
    {
        double v = Math.Sin(i * 12.9898) * 43758.5453;
        return v - Math.Floor(v);
    }

    private static D.Color Hex(string hex) => D.ColorTranslator.FromHtml(hex);
    private static Color Hex2(string hex, double night) => C(SceneMath.Mix(Hex(hex), Hex("#1d2c31"), 0.4 * night));
    private static Color C(D.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
