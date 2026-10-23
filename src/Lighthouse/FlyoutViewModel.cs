using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Lighthouse.Controls;
using Lighthouse.Core;
using Lighthouse.Core.Llm;
using Lighthouse.Core.Alerts;
using Lighthouse.Core.Model;
using Lighthouse.Core.Network;
using Lighthouse.Core.Rendering;
using Lighthouse.Core.Workloads;
using Lighthouse.Core.Tracking;

namespace Lighthouse;

/// <summary>A gauge tile. One object per tile for the flyout's life, so its gauge eases from the last value.</summary>
public sealed class TileVm(string label) : INotifyPropertyChanged
{
    public string Label { get; } = label;
    /// <summary>Opens (or closes) this gauge's last hour under the tiles.</summary>
    public ICommand? Open { get; set; }
    public double Fraction { get; private set; }
    public string GaugeText { get; private set; } = "";
    public string Main { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public IReadOnlyList<double> Spark { get; private set; } = [];
    public double SparkMax { get; private set; } = 100;
    public Brush Accent { get; private set; } = Brushes.Gray;

    /// <summary>The palette key behind <see cref="Accent"/> ("Accent", "Warn", "Hot"), for views that pick their own shade.</summary>
    public string AccentKey { get; private set; } = "Accent";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Set(double fraction, string main, string detail, IReadOnlyList<double> spark, double sparkMax, string accentKey)
    {
        Fraction = Math.Clamp(fraction, 0, 1);
        GaugeText = $"{Fraction * 100:0}%";
        Main = main;
        Detail = detail;
        Spark = spark;
        SparkMax = sparkMax;
        AccentKey = accentKey;
        Accent = Theme.Brush(accentKey);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>
/// A "Using it now" row: CPU, then three more columns, GPU · RAM · VRAM or Disk · Down · Up by the view. Its dot is
/// <see cref="Badge"/> filled, or a <see cref="Ring"/> for plain apps.
/// </summary>
public sealed record RowVm(string Label, string Cpu, string Col2, string Col3, string Col4, string Tip, FontWeight Weight, Brush Badge)
{
    public Brush Ring { get; init; } = Brushes.Transparent;
    /// <summary>Asks the local LLM about this row; null when there is no LLM to ask (and on the rest and total rows).</summary>
    public ICommand? Explain { get; init; }
    public bool CanExplain => Explain is not null;
}

/// <summary>A card under the rows. <see cref="Key"/> names it for its × (see <see cref="ClosedCards"/>).</summary>
public sealed record CardVm(string Title, string Body, Brush Tone, string? ActionText = null, ICommand? Action = null)
{
    public string Key { get; init; } = "";
    public ICommand? Close { get; init; }
    /// <summary>Asks the local LLM about this card; null when there is no LLM to ask.</summary>
    public ICommand? Explain { get; init; }
    public Visibility ActionVisibility => ActionText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ExplainVisibility => Explain is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FooterVisibility => ActionText is null && Explain is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>One line of the scene's legend: a part of the scene and what it shows now.</summary>
public sealed record LegendItemVm(string Name, string Meaning);

/// <summary>One choice of a row of chips (the History page's range, metric and list).</summary>
public sealed record ChipVm(string Label, bool Selected, ICommand Select);

/// <summary>One row of the History page's list; a click opens its detail.</summary>
public sealed record HistoryRowVm(string When, string Title, string Detail, bool Open, ICommand Toggle)
{
    public Visibility DetailVisibility => Open && Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>One question to the local LLM and its answer ("" while it is still answering).</summary>
public sealed record ExplainTurnVm(string Question, string Answer)
{
    public string AnswerShown => Answer.Length > 0 ? Answer : "Asking the local LLM…";
}

/// <summary>One of the speed panel's three numbers: "412" "Mbps" "download", in the colour of its state.</summary>
public sealed record SpeedStatVm(string Value, string Unit, string Label, Brush Ink);

/// <summary>The speed test panel: what it is doing, three numbers, a progress bar while it runs, the line it used.</summary>
public sealed record SpeedPanelVm(string Status, IReadOnlyList<SpeedStatVm> Stats, double Progress, string Detail, string? Note)
{
    public Visibility ProgressVisibility => Progress is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoteVisibility => Note is null ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class RelayCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}

public sealed class FlyoutViewModel : INotifyPropertyChanged
{
    private readonly MonitorEngine _engine;
    private readonly List<ChatTurn> _chat = []; // what the local LLM has been asked and has said
    private IReadOnlyList<ExplainTurnVm> _turns = [];
    private bool _explaining;
    private int _explainRun;
    private string _followUp = "";
    private SpeedPanelVm? _speed;
    private bool _speedTesting;
    private int _speedRun;
    private CancellationTokenSource? _speedCts;
    private string? _speedPhase;
    private DateTime _phaseStarted;
    private double? _ping, _down, _up;
    private SceneSignal? _signal;
    private DateTime _signalUntil;
    private string? _copiedNote;
    private readonly ClosedCards _closedCards;
    private readonly SceneBoats _boats = new();
    /// <summary>When Windows started: closed cards stay closed until it starts again.</summary>
    private static readonly DateTime Boot = ClosedCards.BootTime(DateTime.Now, Environment.TickCount64);

    private readonly Action<bool> _setMini;

    public FlyoutViewModel(MonitorEngine engine, Action<bool> setMini)
    {
        _engine = engine;
        _setMini = setMini;
        _closedCards = new ClosedCards(ClosedCards.Restore(engine.Settings.ClosedCards, engine.Settings.ClosedCardsBoot, Boot));
        SceneLegendCommand = new RelayCommand(() =>
        {
            _engine.Settings.SceneLegend = !_engine.Settings.SceneLegend;
            _engine.SaveSettings();
            Raise(nameof(SceneLegendVisibility));
        });
        Tiles = [_cpu, _gpu, _ram, _vram];
        foreach (var tile in Tiles) tile.Open = new RelayCommand(() => ShowHistory(tile.Label, TimeSpan.FromHours(1)));
        HistoryBackCommand = new RelayCommand(ShowMain);
        AskCommand = new RelayCommand(() => Ask("What's going on?", "What is using the machine, and does anything need attention?", null));
        AskFollowUpCommand = new RelayCommand(AskFollowUp);
        SpeedTestCommand = new RelayCommand(RunSpeedTest);
        SpeedCloseCommand = new RelayCommand(CloseSpeed);
        ExplainCloseCommand = new RelayCommand(() =>
        {
            _explainRun++; // an answer still on its way is dropped
            _chat.Clear();
            _turns = [];
            _explaining = false;
            _followUp = "";
            Raise(string.Empty);
        });
        HistoryCommand = new RelayCommand(() => ShowHistory());
        PauseCommand = new RelayCommand(TogglePause);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Headline { get; private set; } = "Starting…";
    public string MachineLine => _engine.Machine.Line;
    public string Subline { get; private set; } = "";
    private readonly TileVm _cpu = new("CPU"), _gpu = new("GPU"), _ram = new("RAM"), _vram = new("VRAM");

    /// <summary>
    /// One list for the VM's life. A new list on every read made WPF rebuild the gauges each second, so they
    /// swept up from zero instead of easing from their last value.
    /// </summary>
    public IReadOnlyList<TileVm> Tiles { get; }
    public SceneState? Scene { get; private set; }
    public IReadOnlyList<RowVm> Rows { get; private set; } = [];

    /// <summary>"Using it now" by load (CPU, GPU, RAM, VRAM) or by disk and network.</summary>
    public bool IoView
    {
        get => _engine.Settings.UsingView == "io";
        set
        {
            if (value == IoView) return;
            _engine.Settings.UsingView = value ? "io" : "load";
            _engine.SaveSettings();
            if (_last is { } s) Update(s);
        }
    }

    public bool LoadView
    {
        get => !IoView;
        set => IoView = !value;
    }

    // Two lines in the disk and network view, name over unit, so all three columns line up.
    public string Head2 => IoView ? "Disk\nMB/s" : "GPU";
    public string Head3 => IoView ? "Down\nMbps" : "RAM GB";
    public string Head4 => IoView ? "Up\nMbps" : "VRAM";
    public string IoNote { get; private set; } = "";
    public Visibility IoNoteVisibility => IoView && IoNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public IReadOnlyList<CardVm> Cards { get; private set; } = [];
    public string SelfLine { get; private set; } = "";
    public string PauseText { get; private set; } = "Pause alerts";

    /// <summary>Pictures and checks: show the Explain controls as if a local LLM were running.</summary>
    internal bool PreviewLlm { get; set; }
    private bool HasLlm => _engine.Llm is not null || PreviewLlm;
    private bool LlmReady => HasLlm && Game is null;
    /// <summary>The game being played: the engine's, or the latest snapshot's (the same thing, and pictures have only that).</summary>
    private string? Game => _engine.CurrentGame ?? _last?.Game?.Label;

    public IReadOnlyList<ExplainTurnVm> ExplainTurns => _turns;
    public Visibility ExplainVisibility => _turns.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>"What's going on?" shows only when there is a local LLM to ask.</summary>
    public Visibility AskVisibility => HasLlm ? Visibility.Visible : Visibility.Collapsed;
    public bool CanAsk => !_explaining && LlmReady;
    public string AskTip => Game is { } g ? $"Not while {g} is running: the LLM would take GPU time from the game."
        : $"Ask the local LLM{(_engine.Llm is { } l ? $" ({l.AppName})" : "")} what is going on. Free, and it stays on this PC.";
    public string FollowUp
    {
        get => _followUp;
        set
        {
            _followUp = value ?? "";
            Raise(nameof(FollowUpHintVisibility));
        }
    }
    public Visibility FollowUpVisibility => _turns.Count > 0 && !_explaining && LlmReady ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FollowUpHintVisibility => _followUp.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    public SpeedPanelVm? Speed => _speed;
    public Visibility SpeedVisibility => _speed is null ? Visibility.Collapsed : Visibility.Visible;
    public string SpeedCloseTip => _speedTesting ? "Stop the test" : "Close";
    public bool CanSpeedTest => !_speedTesting && _engine.CurrentGame is null;
    public string SpeedButtonText => _speedTesting ? "Testing…" : "Speed test";
    public string SpeedTip => _engine.CurrentGame is { } game ? $"Not while {game} is running."
        : $"Test the internet connection's download, upload and ping (about 20 seconds, up to {(SpeedTest.MaxDownBytes + SpeedTest.MaxUpBytes) / 1_000_000} MB, through speed.cloudflare.com).";

    /// <summary>The always-on-top mini monitor (off until it is switched on).</summary>
    /// <summary>The Dark mode switch: Windows' app theme until it is flipped, then the choice is kept ("theme").</summary>
    public bool DarkMode
    {
        get => !Theme.IsLight;
        set
        {
            if (value == DarkMode) return;
            _engine.Settings.Theme = value ? "dark" : "light";
            _engine.SaveSettings();
            Theme.Chosen = !value;
            Theme.Apply();
            if (_last is { } s) Update(s); // the rows' dots and the tiles' colours come from the palette
        }
    }

    // The scene's legend: what each part of the scene shows, with its reading now. Opened with the scene's "?".
    public Visibility SceneLegendVisibility => _engine.Settings.SceneLegend ? Visibility.Visible : Visibility.Collapsed;
    public ICommand SceneLegendCommand { get; }
    public IReadOnlyList<LegendItemVm> SceneLegend { get; private set; } = [];

    public bool MiniMonitor
    {
        get => _engine.Settings.MiniMonitor;
        set
        {
            if (value != MiniMonitor) _setMini(value);
        }
    }

    public void RaiseMini() => Raise(nameof(MiniMonitor));

    // The History page: the flyout's drill-through (History, or a click on a gauge), in place of the main page.
    private bool _historyOpen;
    private TimeSpan _historyRange = TimeSpan.FromHours(24);
    private string _historyMetric = "CPU";
    private string _historyTab = "events";
    private readonly HashSet<string> _openRows = [];
    private DateTime _historyChartBuilt = DateTime.MinValue;

    public bool HistoryOpen => _historyOpen;
    public Visibility MainVisibility => _historyOpen ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HistoryVisibility => _historyOpen ? Visibility.Visible : Visibility.Collapsed;
    public ICommand HistoryBackCommand { get; }
    public IReadOnlyList<ChipVm> HistoryRanges { get; private set; } = [];
    public IReadOnlyList<ChipVm> HistoryMetrics { get; private set; } = [];
    public IReadOnlyList<ChipVm> HistoryTabs { get; private set; } = [];
    public IReadOnlyList<DateTime> HistoryTimes { get; private set; } = [];
    public IReadOnlyList<ChartSeries> HistorySeries { get; private set; } = [];
    public DateTime? HistoryFrom { get; private set; }
    public DateTime? HistoryTo { get; private set; }
    public double HistoryMaximum { get; private set; }
    public string HistoryUnit { get; private set; } = "";
    public bool HistoryFitLow { get; private set; }
    public string HistorySummary { get; private set; } = "";
    public Visibility HistorySummaryVisibility => HistorySummary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<HistoryRowVm> HistoryRows { get; private set; } = [];
    public string HistoryEmpty { get; private set; } = "";
    public Visibility HistoryEmptyVisibility => HistoryRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static readonly (string Label, TimeSpan Span)[] Ranges =
        [("1 hour", TimeSpan.FromHours(1)), ("24 hours", TimeSpan.FromHours(24)), ("7 days", TimeSpan.FromDays(7))];
    private static readonly string[] Metrics = ["CPU", "GPU", "RAM", "VRAM", "Temps"];
    private static readonly (string Key, string Label)[] Tabs =
        [("events", "Events"), ("games", "Games"), ("jobs", "Jobs"), ("speed", "Speed tests")];

    /// <summary>Opens the History page, on a metric and range when given (a gauge opens its own last hour).</summary>
    public void ShowHistory(string? metric = null, TimeSpan? range = null)
    {
        _historyOpen = true;
        if (metric is not null && Metrics.Contains(metric)) _historyMetric = metric;
        if (range is { } r) _historyRange = r;
        BuildHistory(DateTime.Now, rows: true);
        Raise(string.Empty);
    }

    /// <summary>Back to the main page (the back arrow, Escape, or the tray icon).</summary>
    public void ShowMain()
    {
        if (!_historyOpen) return;
        _historyOpen = false;
        Raise(string.Empty);
    }

    private void BuildHistory(DateTime now, bool rows)
    {
        HistoryRanges = Ranges.Select(r => new ChipVm(r.Label, r.Span == _historyRange, new RelayCommand(() =>
        {
            _historyRange = r.Span;
            BuildHistory(DateTime.Now, rows: false);
            Raise(string.Empty);
        }))).ToList();
        HistoryMetrics = Metrics.Select(m => new ChipVm(m, m == _historyMetric, new RelayCommand(() =>
        {
            _historyMetric = m;
            BuildHistory(DateTime.Now, rows: false);
            Raise(string.Empty);
        }))).ToList();
        HistoryTabs = Tabs.Select(t => new ChipVm(t.Label, t.Key == _historyTab, new RelayCommand(() =>
        {
            _historyTab = t.Key;
            BuildHistory(DateTime.Now, rows: true);
            Raise(string.Empty);
        }))).ToList();
        BuildHistoryChart(now);
        if (rows) BuildHistoryRows(now);
    }

    /// <summary>
    /// The chart: minute averages over the range (for the last hour, the last 10 minutes second by second too),
    /// bucketed to the width keeping each bucket's peak (so it reaches the "max" under it), with a break wherever
    /// nothing was recorded.
    /// </summary>
    private void BuildHistoryChart(DateTime now)
    {
        _historyChartBuilt = now;
        var from = now - _historyRange;
        var recent = _historyRange <= TimeSpan.FromHours(1) ? _engine.Recent(TimeSpan.FromMinutes(10)) : [];
        DateTime cut = recent.Count > 0 ? recent[0].Time : DateTime.MaxValue;
        var minutes = _engine.History.Minutes(from).Where(m => m.Time < cut).ToList();
        var times = minutes.Select(m => m.Time).Concat(recent.Select(r => r.Time)).ToList();
        (string Name, string Brush, Func<Core.History.MinuteRow, double?> Minute, Func<Snapshot, double?> Second)[] lines = _historyMetric switch
        {
            "GPU" => [("GPU %", "Accent", m => m.Gpu, r => r.GpuLoad)],
            "RAM" => [("RAM GB", "Accent", m => m.RamGb, r => r.RamUsedGb)],
            "VRAM" => [("VRAM GB", "Accent", m => m.VramGb, r => r.VramUsedGb)],
            "Temps" =>
            [
                ("CPU °C", "Accent", m => m.CpuTemp, r => r.CpuTemp),
                ("GPU °C", "Series2", m => m.GpuTemp, r => r.GpuTemp),
                ("GPU hotspot °C", "Hot", m => m.GpuHotspot, r => r.GpuHotspot),
            ],
            _ => [("CPU %", "Accent", m => m.Cpu, r => r.CpuLoad)],
        };
        var raw = lines.Select(l => (IReadOnlyList<double?>)minutes.Select(l.Minute).Concat(recent.Select(l.Second)).ToList()).ToList();
        var (bucketTimes, bucketed) = Core.History.ChartData.Bucket(times, raw, from, now, 240, TimeSpan.FromMinutes(3), peaks: true);
        HistoryTimes = bucketTimes;
        HistorySeries = lines.Select((l, i) => new ChartSeries(l.Name, Theme.Brush(l.Brush), bucketed[i])).ToList();
        HistoryFrom = from;
        HistoryTo = now;
        (HistoryMaximum, HistoryUnit, HistoryFitLow) = _historyMetric switch
        {
            "RAM" => (Math.Ceiling(_last?.RamTotalGb ?? 0), " GB", false),
            "VRAM" => (Math.Ceiling(_last?.VramTotalGb ?? 0), " GB", false),
            "Temps" => (0.0, "°", true),
            _ => (100.0, "%", false),
        };
        var known = raw.Select(r => r.Where(v => v is not null).Select(v => v!.Value).ToList()).ToList();
        if (_historyMetric == "Temps")
        {
            var peaks = lines.Select((l, i) => known[i].Count > 0 ? $"{l.Name.Replace(" °C", "")} {known[i].Max():0}°C" : null).OfType<string>().ToList();
            HistorySummary = peaks.Count > 0 ? "hottest: " + string.Join(" · ", peaks) : "";
        }
        else
        {
            string Fmt(double v) => HistoryUnit == "%" ? $"{v:0}%" : $"{v:0.0}{Nb}GB";
            HistorySummary = known[0].Count == 0 ? ""
                : $"min {Fmt(known[0].Min())} · average {Fmt(known[0].Average())} · max {Fmt(known[0].Max())}";
        }
    }

    /// <summary>The list under the chart: events, games, job runs or speed tests, newest first; a row opens its detail.</summary>
    private void BuildHistoryRows(DateTime now)
    {
        var history = _engine.History;
        string When(DateTime t) => t.Date == now.Date ? t.ToString("HH:mm") : now - t < TimeSpan.FromDays(6) ? t.ToString("ddd HH:mm") : t.ToString("d MMM");
        HistoryRowVm Row(string key, DateTime at, string title, string detail) =>
            new(When(at), title, detail, _openRows.Contains(key), new RelayCommand(() =>
            {
                if (!_openRows.Remove(key)) _openRows.Add(key);
                BuildHistoryRows(DateTime.Now);
                Raise(nameof(HistoryRows));
            }));
        switch (_historyTab)
        {
            case "games":
                HistoryRows = history.Games(50)
                    .Select(g => Row($"g:{g.Start.Ticks}", g.Start, $"{g.Game} · {Durations.Format(g.Duration)}", g.Summary())).ToList();
                HistoryEmpty = "No games yet. A game's session shows here when it closes.";
                break;
            case "jobs":
                HistoryRows = history.JobRuns(limit: 100)
                    .Select(r => Row($"j:{r.Start.Ticks}:{r.Label}", r.Start, $"{r.Label} · {Durations.Format(r.Duration)}",
                        r.Exit is { } code ? JobText.Describe(code) : "ended, exit code unknown")).ToList();
                HistoryEmpty = "No job runs yet. A watched job's run shows here when it finishes.";
                break;
            case "speed":
                var plan = _engine.Settings.InternetPlan;
                HistoryRows = Enumerable.Reverse(history.SpeedTests(now.AddDays(-365)))
                    .Select(t => Row($"s:{t.Time.Ticks}", t.Time,
                        $"{SpeedTest.Format(t.DownMbps)} down · {SpeedTest.Format(t.UpMbps)} up · {t.PingMs:0}{Nb}ms",
                        string.Join(" · ", new[]
                        {
                            t.Connection,
                            t.BackgroundMbps is >= 2 and var b ? $"other traffic on the line: {b:0} Mbps" : null,
                            Insights.PlanShare(t.DownMbps, t.UpMbps, plan),
                        }.Where(p => p is not null)))).ToList();
                HistoryEmpty = "No speed tests yet. Press Speed test on the main page.";
                break;
            default:
                // Speed tests, games and job runs have their own lists.
                HistoryRows = history.Events(300).Where(e => e.Kind is not ("network" or "game" or "job")).Take(100)
                    .Select(e => Row($"e:{e.Time.Ticks}:{e.Title}", e.Time, e.Title, e.Detail)).ToList();
                HistoryEmpty = "Nothing yet. Alerts, health checks and the local LLM's stops show here.";
                break;
        }
    }

    public ICommand AskCommand { get; }
    public ICommand AskFollowUpCommand { get; }
    public ICommand ExplainCloseCommand { get; }
    public ICommand SpeedCloseCommand { get; }
    public ICommand SpeedTestCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand PauseCommand { get; }

    /// <summary>The snapshot on screen, so a view switch redraws at once from what is shown.</summary>
    private Snapshot? _last;

    public void Update(Snapshot s)
    {
        _last = s;
        var now = DateTime.Now;
        var recent = _engine.Recent(TimeSpan.FromSeconds(60));
        Headline = Core.Headline.Build(s, _engine.ActiveAlerts, now);

        _cpu.Set(s.CpuLoad / 100, Deg(s.CpuTemp) ?? "—",
            Join(Watts(s.CpuPower), s.CpuClockMhz is { } mhz ? $"{mhz / 1000:0.0}{Nb}GHz" : null),
            recent.Select(r => r.CpuLoad).ToList(), 100, HeatKey(TrayIconRenderer.CpuHeat(s.CpuTemp)));
        _gpu.Set(s.GpuLoad / 100, Deg(s.GpuTemp) ?? "—",
            Join(s.GpuHotspot is { } hs ? $"hotspot{Nb}{hs:0}°C" : null, Watts(s.GpuPower)),
            recent.Select(r => r.GpuLoad).ToList(), 100, HeatKey(TrayIconRenderer.GpuHeat(s.GpuTemp, s.GpuHotspot)));
        _ram.Set(s.RamTotalGb > 0 ? s.RamUsedGb / s.RamTotalGb : 0, $"{s.RamUsedGb:0.0}{Nb}GB",
            Join($"of{Nb}{s.RamTotalGb:0}{Nb}GB", $"commit{Nb}{s.CommitPct:0}%"),
            recent.Select(r => r.RamUsedGb).ToList(), s.RamTotalGb, "Accent");
        _vram.Set(s.VramTotalGb > 0 ? s.VramUsedGb / s.VramTotalGb : 0, $"{s.VramUsedGb:0.0}{Nb}GB",
            $"of{Nb}{s.VramTotalGb:0}{Nb}GB", recent.Select(r => r.VramUsedGb).ToList(), Math.Max(1, s.VramTotalGb), "Accent");

        Scene = new SceneState(
            s.CpuLoad,
            TrayIconRenderer.CpuHeat(s.CpuTemp),
            s.GpuLoad,
            s.RamTotalGb > 0 ? s.RamUsedGb / s.RamTotalGb : 0,
            // Watched jobs and busy programs sail, each with its legend colour for a sail.
            _boats.Pick(s.AllWorkloads, now, Environment.ProcessorCount)
                .Select(w => new SceneBoat(w.Key, w.Kind == WorkloadKind.Job ? SceneMath.ShortRunLabel(w.Label) : ShortName(w.Label),
                    Math.Min(40, 8 + 0.8 * w.CpuPct), SailHex(w.Kind)))
                .ToList(),
            s.Game?.Label,
            Moon: s.LocalLlm is not null,
            MoonThinking: s.LocalLlm is { GpuPct: >= 30 },
            Signal: _speedTesting || now < _signalUntil ? _signal : null,
            // A storm while a warning is up, lightning for a critical one, fog from 85% memory commit.
            Weather: new SceneWeather(
                Storm: _engine.ActiveAlerts.Any(a => a.Severity >= Severity.Warning),
                Lightning: _engine.ActiveAlerts.Any(a => a.Severity == Severity.Critical),
                Fog: Math.Clamp((s.CommitPct - 85) / 15, 0, 1)),
            NetDownMbps: s.NetDownBytesPerSec * 8 / 1e6,
            NetUpMbps: s.NetUpBytesPerSec * 8 / 1e6,
            DiskActivity: s.DiskActivePct / 100);
        SceneLegend = BuildLegend(s, Scene.Boats.Count);

        Rows = IoView ? IoRows(s) : LoadRows(s);
        IoNote = s.IoTraced ? ""
            : "Network per program and exact disk use come from the sensor helper. Without it, disk counts every read and write a program makes.";

        Cards = BuildCards(s, now);
        if (_historyOpen && now - _historyChartBuilt >= TimeSpan.FromSeconds(10)) BuildHistoryChart(now);
        SelfLine = s.Self is { } self ? $"Lighthouse itself: {self.CpuPct:0.0}% CPU, {self.RamGb * 1024:0} MB." : "";
        Subline = Join(s.NvmeTemp is { } ssd ? $"SSD {ssd:0}°C" : null, s.DimmTemp is { } dimm ? $"RAM {dimm:0}°C" : null,
            $"updated {now:T}", s.FullSensors ? null : "no CPU temperature: the sensor helper isn't running");
        if (Log.LastError is { } logError) Subline += $" · log failing: {logError}";
        bool paused = _engine.Settings.AlertsPausedUntil is { } until && until > now;
        PauseText = paused ? "Resume alerts" : "Pause alerts 1 h";
        Raise(string.Empty);
    }

    private List<RowVm> LoadRows(Snapshot s)
    {
        var rows = s.Workloads.Select(w => Row(w, LoadColumns(w)) with { Explain = ExplainRow(w) }).ToList();
        if (s.Rest is { } rest) rows.Add(Row(rest, LoadColumns(rest)) with { Badge = Brushes.Transparent, Ring = Brushes.Transparent, Tip = rest.Note ?? "" });
        rows.Add(new RowVm("Total", $"{s.CpuLoad:0.0}%", $"{s.GpuLoad:0}%", $"{s.RamUsedGb:0.0}", $"{s.VramUsedGb:0.0}",
            "The tiles' totals. The rows above add up to these.", FontWeights.SemiBold, Brushes.Transparent));
        return rows;
    }

    /// <summary>The busiest programs by disk and network, then what is left, then the totals they add up to.</summary>
    private List<RowVm> IoRows(Snapshot s)
    {
        var rows = s.IoWorkloads.Select(w => Row(w, IoColumns(w, s.IoTraced)) with { Explain = ExplainRow(w) }).ToList();
        if (s.IoRest is { } rest)
            rows.Add(Row(rest, IoColumns(rest, true)) with { Badge = Brushes.Transparent, Ring = Brushes.Transparent, Tip = rest.Note ?? "" });
        double down = s.NetDownBytesPerSec * 8 / 1e6, up = s.NetUpBytesPerSec * 8 / 1e6;
        rows.Add(new RowVm("Total", $"{s.CpuLoad:0.0}%", Rate(s.DiskMBps, zero: "0"), Rate(down, zero: "0"), Rate(up, zero: "0"),
            "Every program's disk reads and writes, and what the network adapters moved. The rows above add up to these.",
            FontWeights.SemiBold, Brushes.Transparent));
        return rows;
    }

    private static (string, string, string) LoadColumns(WorkloadUsage w) => (
        double.IsNaN(w.GpuPct) ? "—" : w.GpuPct < 0.5 ? "" : $"{w.GpuPct:0}%",
        $"{w.RamGb:0.0}",
        w.VramGb < 0.05 ? "" : $"{w.VramGb:0.0}");

    /// <summary>Disk in MB/s, network in Mbps; per program network is only known with the helper's trace.</summary>
    private static (string, string, string) IoColumns(WorkloadUsage w, bool traced) => (
        Rate(w.DiskMBps),
        traced ? Rate(w.NetDownMbps) : "",
        traced ? Rate(w.NetUpMbps) : "");

    /// <summary>"0.4", "12", "412"; blank below a twentieth.</summary>
    public static string Rate(double value, string zero = "") =>
        value < 0.05 ? zero : value < 10 ? $"{value:0.0}" : $"{value:0}";

    private RowVm Row(WorkloadUsage w, (string, string, string) columns) => new(
        w.Label,
        $"{w.CpuPct:0.0}%",
        columns.Item1,
        columns.Item2,
        columns.Item3,
        $"{w.Processes} process{(w.Processes == 1 ? "" : "es")}" + (w.Started is { } st && w.Kind is WorkloadKind.Job or WorkloadKind.Game
            ? $" · started {st:t}" : ""),
        FontWeights.Normal,
        BadgeKey(w.Kind) is { } key ? Theme.Brush(key) : Brushes.Transparent)
    {
        Ring = BadgeKey(w.Kind) is null ? Theme.Brush("Muted") : Brushes.Transparent,
    };

    /// <summary>The dot before each row, one colour per legend group; null is a plain app (an empty ring).</summary>
    public static string? BadgeKey(WorkloadKind kind) =>
        WorkloadKinds.IsAi(kind) ? "Accent"
        : WorkloadKinds.IsJob(kind) ? "Good"
        : kind == WorkloadKind.Game ? "Warn"
        : kind == WorkloadKind.Browser ? "Web"
        : null;

    /// <summary>A boat's sail in its legend colour, the dark palette's (lighter) shade so it shows on the sea; white for an app.</summary>
    private static string? SailHex(WorkloadKind kind) => BadgeKey(kind) switch
    {
        "Accent" => "#7fb3c2",
        "Good" => "#8fb386",
        "Web" => "#e094bf",
        _ => null,
    };

    /// <summary>A name tag short enough for the sea.</summary>
    private static string ShortName(string label) => label.Length <= 18 ? label : label[..17].TrimEnd() + "…";

    /// <summary>The scene's parts and what each shows now.</summary>
    private List<LegendItemVm> BuildLegend(Snapshot s, int boats)
    {
        double down = s.NetDownBytesPerSec * 8 / 1e6, up = s.NetUpBytesPerSec * 8 / 1e6;
        var alerts = _engine.ActiveAlerts;
        string weather = alerts.Any(a => a.Severity == Severity.Critical) ? "lightning now: a critical alert is up"
            : alerts.Any(a => a.Severity >= Severity.Warning) ? "a storm now: a warning is up"
            : s.CommitPct >= 85 ? "fog now: memory is filling"
            : "clear now";
        return
        [
            new("Beam", $"CPU, {s.CpuLoad:0}% now. It turns faster with load and warms toward red as the CPU heats up."),
            new("Sea", $"RAM sets its height ({(s.RamTotalGb > 0 ? s.RamUsedGb / s.RamTotalGb * 100 : 0):0}% now); GPU load its waves ({s.GpuLoad:0}%)."),
            new("Boats", (boats == 1 ? "1 now" : $"{boats} now") +
                         ": watched jobs and busy programs. The sail is the program's dot colour in Using it now."),
            new("Ship", s.Game is { } game ? $"{game.Label}, the game running now." : "A game, when one runs."),
            new("Moon", s.LocalLlm is null ? "The local LLM, when one runs." : "The local LLM. It glows while it works."),
            new("Gulls", $"The network: in with downloads ({down:0} Mbps now), out with uploads ({up:0} Mbps)."),
            new("Foam", $"The disk: {s.DiskActivePct:0}% busy now."),
            new("Weather", $"Alerts and memory: {weather}. Heat shimmer by the lantern means the CPU is hot."),
            new("Light", "A speed test, crossing to the beacon on the far shore."),
        ];
    }

    private List<CardVm> BuildCards(Snapshot s, DateTime now)
    {
        var cards = new List<CardVm>();
        var settings = _engine.Settings;

        foreach (var alert in _engine.ActiveAlerts.Where(a => a.Id != "llm-vram"))
            cards.Add(new CardVm(alert.Title, alert.Message, alert.Severity == Severity.Critical ? Theme.Brush("Hot") : Theme.Brush("Warn"))
                { Key = "alert:" + alert.Id });

        if (_engine.CurrentGame is { } game)
        {
            string ccd = s.Ccd switch
            {
                CcdState.OtherParked => "V-Cache CCD active, CCD1 parked.",
                CcdState.BothActive => "Both CCDs active.",
                CcdState.CacheParked => "V-Cache CCD parked (frequency mode).",
                _ => "",
            };
            string body = Join($"GPU{Nb}{s.GpuLoad:0}%", Deg(s.GpuTemp), s.GpuHotspot is { } h ? $"hotspot{Nb}{h:0}°C" : null,
                Deg(s.CpuTemp) is { } c ? $"CPU {c}" : null) + ". " + ccd;
            if (_engine.BothCcdsActiveWarning)
                body += " Game Bar may not have recognised it: AMD's driver only parks CCD1 for games it knows.";
            var llm = s.LocalLlm;
            bool offerStop = llm is not null && llm.VramGb >= settings.Thresholds.LlmVramContentionGb;
            if (offerStop) body += $" The local LLM holds {llm!.VramGb:0.0} GB of VRAM.";
            cards.Add(new CardVm($"{game} · {Core.Headline.Elapsed(_engine.GameStarted, now)}", body,
                _engine.BothCcdsActiveWarning || offerStop ? Theme.Brush("Warn") : Theme.Brush("Accent"),
                offerStop ? "Stop local LLM" : null, offerStop ? new RelayCommand(StopLlm) : null)
                { Key = $"game:{game}:{_engine.GameStarted?.Ticks}" });
        }

        if (settings.PausedLlmStartCommand is { } start)
        {
            if (_engine.Llm is not null)
            {
                _engine.ForgetPausedLlm(); // it is back
            }
            else
            {
                string? account = LocalLlm.RunasUser(start);
                cards.Add(new CardVm("The local LLM is stopped",
                    _copiedNote ?? (account is not null
                        ? $"Lighthouse can't start it: it runs as {account}, and runas asks for that account's password. Copy the start command and paste it into PowerShell."
                        : "Copy the start command and paste it into PowerShell to start it again."),
                    Theme.Brush("Accent"), "Copy start command", new RelayCommand(() =>
                    {
                        Clipboard.SetText(start);
                        _copiedNote = account is not null
                            ? $"Copied. Paste it into a normal PowerShell window and enter {account}'s password."
                            : "Copied. Paste it into a normal PowerShell window.";
                    }))
                    { Key = "llm-stopped:" + start });
            }
        }

        foreach (var run in s.AllWorkloads.Where(w => w.Kind == WorkloadKind.Job))
        {
            string body = Join(Core.Headline.Elapsed(run.Started, now) + " in", Core.Headline.CoresText(run.CpuPct, Nb),
                $"{run.RamGb:0.0}{Nb}GB");
            if (_engine.JobProgress(run.Label, run.Started) is { } progress) body += "\n" + progress;
            cards.Add(new CardVm(run.Label, body, Theme.Brush("Good")) { Key = run.Key });
        }

        if (_engine.Digest is { } digest && now - digest.Made < TimeSpan.FromDays(2))
            cards.Add(new CardVm("Your week", string.Join("\n", digest.Lines), Theme.Brush("Accent")) { Key = $"digest:{digest.Made.Ticks}" });

        var dismissed = _engine.Settings.DismissedHealth;
        var shown = _engine.Health.Where(h => !dismissed.Contains(h.Id)).ToList();
        var failing = shown.Where(h => !h.Ok).ToList();
        foreach (var h in failing)
            cards.Add(new CardVm(h.Title, h.Detail, Theme.Brush("Warn"), "Don't show again", new RelayCommand(() => Dismiss(h.Id)))
                { Key = "health:" + h.Id });
        if (shown.Count > 0 && failing.Count == 0)
        {
            int hidden = _engine.Health.Count - shown.Count;
            string title = $"All {shown.Count} checks pass" + (hidden > 0 ? $" ({hidden} dismissed)" : "");
            cards.Add(new CardVm(title, string.Join(" ", shown.Select(h => h.Detail)), Theme.Brush("Good")) { Key = "health-ok" });
        }

        return _closedCards.Visible(cards, c => c.Key)
            .Select(c => c with
            {
                Close = new RelayCommand(() => CloseCard(c.Key)),
                Explain = LlmReady && !c.Key.StartsWith("llm-stopped", StringComparison.Ordinal)
                    ? new RelayCommand(() => Ask($"About \u201c{c.Title}\u201d", "What does this mean, and should I do anything?",
                        $"The question is about the card \"{c.Title}\", which says: {c.Body}"))
                    : null,
            })
            .ToList();
    }

    private void CloseCard(string key)
    {
        _closedCards.Close(key);
        _engine.Settings.ClosedCards = [.. _closedCards.Keys];
        _engine.Settings.ClosedCardsBoot = Boot;
        _engine.SaveSettings();
        if (_engine.Latest is { } s) Update(s);
    }

    private void Dismiss(string healthId)
    {
        _engine.DismissHealth(healthId);
        if (_engine.Latest is { } s) Update(s);
    }

    private void StopLlm()
    {
        try
        {
            if (_engine.StopLlm() is { } start)
            {
                Clipboard.SetText(start);
                _copiedNote = "Stopped. Its start command is on your clipboard for after the game.";
            }
        }
        catch (Exception e)
        {
            Log.Write($"stop LLM failed: {e.Message}");
        }
    }

    /// <summary>A row's Explain: what the program is and whether it is a problem, with all its numbers.</summary>
    private RelayCommand? ExplainRow(WorkloadUsage w)
    {
        if (!LlmReady) return null;
        string kind = w.Kind switch
        {
            WorkloadKind.Game => "a game",
            WorkloadKind.Job => "a script the user asked Lighthouse to watch",
            WorkloadKind.Service => "a server the user asked Lighthouse to watch",
            WorkloadKind.LocalLlm => "the local LLM",
            WorkloadKind.ClaudeCode or WorkloadKind.ClaudeApp or WorkloadKind.AiApp => "an AI app",
            WorkloadKind.Windows => "part of Windows",
            _ => "an app",
        };
        string gpu = double.IsNaN(w.GpuPct) ? "" : $", GPU {w.GpuPct:0}%";
        string numbers = $"CPU {w.CpuPct:0.0}% of the machine{gpu}, RAM {w.RamGb:0.0} GB, VRAM {w.VramGb:0.0} GB, " +
                         $"disk {w.DiskMBps:0.0} MB/s, network {w.NetDownMbps:0.0} Mbps down and {w.NetUpMbps:0.0} up, " +
                         $"{w.Processes} process{(w.Processes == 1 ? "" : "es")}";
        return new RelayCommand(() => Ask($"About {w.Label}", "What is this, and is it a problem?",
            $"The question is about \"{w.Label}\" ({kind}) in Using it now: {numbers}."));
    }

    private void AskFollowUp()
    {
        string question = _followUp.Trim();
        if (question.Length == 0) return;
        _followUp = "";
        Ask(question, question, null, followUp: true);
    }

    /// <summary>
    /// Asks the local LLM. A new question (the button, a card's or a row's Explain) starts a new conversation with
    /// the current readings; a follow-up continues it. <paramref name="shown"/> is the question as the panel shows it.
    /// </summary>
    private async void Ask(string shown, string question, string? about, bool followUp = false)
    {
        if (!LlmReady || _explaining) return;
        if (!followUp)
        {
            _chat.Clear();
            _turns = [];
        }
        string content = _chat.Count > 0 ? question
            : _engine.ExplainDigest() + "\n" + (about is null ? "" : about + "\n") + "Question: " + question;
        _chat.Add(new ChatTurn("user", content));
        _turns = [.. _turns, new ExplainTurnVm(shown, "")];
        int run = ++_explainRun;
        _explaining = true;
        Raise(string.Empty);
        LlmAnswer answer;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            answer = await _engine.AskLlmAsync([.. _chat], cts.Token);
        }
        catch (Exception e)
        {
            answer = new($"Could not ask the local LLM: {e.Message}", false);
        }
        if (run != _explainRun) return; // closed while it was answering
        if (answer.Ok) _chat.Add(new ChatTurn("assistant", answer.Text));
        else _chat.RemoveAt(_chat.Count - 1); // it never got through: a follow-up shouldn't carry it
        _turns = [.. _turns.Take(_turns.Count - 1), _turns[^1] with { Answer = answer.Text }];
        _explaining = false;
        Raise(string.Empty);
    }

    /// <summary>Pictures and checks: a conversation as the panel would show it.</summary>
    internal void ShowExplainPreview(params (string Question, string Answer)[] turns)
    {
        _turns = turns.Select(t => new ExplainTurnVm(t.Question, t.Answer)).ToList();
        Raise(string.Empty);
    }

    private async void RunSpeedTest()
    {
        int run = ++_speedRun;
        _speedTesting = true;
        _ping = _down = _up = null;
        _speedPhase = null;
        OnSpeedProgress(new SpeedProgress("Ping", null));
        // Progress made here reports on the UI thread.
        var progress = new Progress<SpeedProgress>(p =>
        {
            if (run == _speedRun) OnSpeedProgress(p);
        });
        SpeedOutcome outcome;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _speedCts = cts;
            outcome = await _engine.SpeedTestAsync(progress, cts.Token);
        }
        catch (Exception e)
        {
            outcome = new SpeedOutcome(null, $"The speed test failed: {e.Message}");
        }
        _speedCts = null;
        _speedTesting = false;
        if (run != _speedRun) // its × stopped it
        {
            Raise(string.Empty);
            return;
        }
        if (outcome.Result is { } r)
        {
            (_ping, _down, _up) = (r.PingMs, r.DownMbps, r.UpMbps);
            _speed = SpeedPanel($"Speed test · {DateTime.Now:t}", progressDone: 1, WithPlan(r), r.BusyNote.Trim() is { Length: > 0 } n ? n : null);
            _signal = new SceneSignal(SignalPhase.Done, null, $"↓ {SpeedTest.Format(r.DownMbps)}  ↑ {SpeedTest.Format(r.UpMbps)}");
            _signalUntil = DateTime.Now.AddSeconds(8);
        }
        else
        {
            _speed = SpeedPanel(outcome.Error ?? "The speed test failed.", progressDone: 1, "", null);
            _signal = null;
        }
        Raise(string.Empty);
    }

    private void OnSpeedProgress(SpeedProgress p)
    {
        if (p.Phase != _speedPhase)
        {
            _speedPhase = p.Phase;
            _phaseStarted = DateTime.Now;
        }
        if (p.PingMs is { } ping) _ping = ping;
        if (p.Mbps is { } mbps)
        {
            if (p.Phase == "Download") _down = mbps;
            else if (p.Phase == "Upload") _up = mbps;
        }
        // The bar follows the test's own clock: ping is quick, download runs 8 s, upload 6 s.
        double t = (DateTime.Now - _phaseStarted).TotalSeconds;
        double done = p.Phase switch
        {
            "Download" => 0.1 + 0.5 * Math.Min(1, t / 8),
            "Upload" => 0.6 + 0.4 * Math.Min(1, t / 6),
            _ => 0.02 + 0.08 * Math.Min(1, t / 2),
        };
        _speed = SpeedPanel($"Measuring {p.Phase.ToLowerInvariant()}…", Math.Min(done, 0.99), "", null);
        _signal = p.Phase switch
        {
            "Download" => new SceneSignal(SignalPhase.Download, _down, _down is { } d ? $"↓ {SpeedTest.Format(d)}" : "↓ …"),
            "Upload" => new SceneSignal(SignalPhase.Upload, _up, _up is { } u ? $"↑ {SpeedTest.Format(u)}" : "↑ …"),
            _ => new SceneSignal(SignalPhase.Ping, null, _ping is { } pm ? $"{pm:0} ms" : "Ping…"),
        };
        Raise(nameof(Speed));
        Raise(nameof(SpeedVisibility));
        Raise(nameof(SpeedCloseTip));
        Raise(nameof(SpeedButtonText));
        Raise(nameof(CanSpeedTest));
    }

    /// <summary>The panel from what is known so far: the phase under way in the accent colour, numbers not in yet as dashes.</summary>
    private SpeedPanelVm SpeedPanel(string status, double progressDone, string detail, string? note)
    {
        Brush Ink(string phase, double? value) => _speedTesting && _speedPhase == phase ? Theme.Brush("Accent")
            : value is null ? Theme.Brush("Muted") : Theme.Brush("Ink");
        SpeedStatVm Rate(string phase, double? mbps, string label) => mbps is { } m && SpeedTest.Parts(m) is var (v, unit)
            ? new(v, unit, label, Ink(phase, mbps))
            : new("—", "Mbps", label, Ink(phase, null));
        return new SpeedPanelVm(status,
        [
            Rate("Download", _down, "download"),
            Rate("Upload", _up, "upload"),
            _ping is { } ping ? new($"{ping:0}", "ms", "ping", Ink("Ping", ping)) : new("—", "ms", "ping", Ink("Ping", null)),
        ], progressDone, detail, note);
    }

    /// <summary>The speed panel's ×: stops a test under way, or closes the result.</summary>
    private void CloseSpeed()
    {
        _speedRun++;
        _speedCts?.Cancel();
        _speed = null;
        _signal = null;
        _signalUntil = DateTime.MinValue;
        Raise(string.Empty);
    }

    /// <summary>For --text-check and previews: switch the "Using it now" view without saving it.</summary>
    internal void ShowView(bool io)
    {
        _engine.Settings.UsingView = io ? "io" : "load";
        if (_last is { } s) Update(s);
    }

    /// <summary>For --text-check and previews: show a finished result.</summary>
    internal void ShowSpeedResult(SpeedResult r)
    {
        (_ping, _down, _up) = (r.PingMs, r.DownMbps, r.UpMbps);
        _speed = SpeedPanel($"Speed test · {DateTime.Now:t}", 1, WithPlan(r), r.BusyNote.Trim() is { Length: > 0 } n ? n : null);
        Raise(string.Empty);
    }

    /// <summary>The connection line, and how much of the internet plan the test got when a plan is set.</summary>
    private string WithPlan(SpeedResult r) =>
        Insights.PlanShare(r.DownMbps, r.UpMbps, _engine.Settings.InternetPlan) is { } share ? $"{r.ConnectionLine}\n{share}" : r.ConnectionLine;

    private void TogglePause()
    {
        bool paused = _engine.Settings.AlertsPausedUntil is { } until && until > DateTime.Now;
        if (paused)
        {
            _engine.Settings.AlertsPausedUntil = null;
            _engine.SaveSettings();
        }
        else
        {
            _engine.PauseAlerts(TimeSpan.FromHours(1));
        }
        PauseText = paused ? "Pause alerts 1 h" : "Resume alerts";
        Raise(nameof(PauseText));
    }

    private static string HeatKey(Heat heat) => heat switch
    {
        Heat.Hot => "Hot",
        Heat.Warm => "Warn",
        _ => "Accent",
    };

    private static string? Deg(double? v) => v is { } x ? $"{x:0}°C" : null;
    /// <summary>A non-breaking space: keeps a number with its unit ("335 W") when a line wraps.</summary>
    private const string Nb = "\u00A0";

    private static string? Watts(double? v) => v is { } x ? $"{x:0}{Nb}W" : null;
    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
