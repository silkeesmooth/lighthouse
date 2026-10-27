using Lighthouse.Core.Model;

namespace Lighthouse.Core.Tracking;

public sealed record GameSession(
    string Game,
    DateTime Start,
    DateTime End,
    double GpuAvg,
    double? GpuTempMax,
    double? HotspotMax,
    double? CpuTempMax,
    double? ParkedPct,
    double VramPeakGb)
{
    public TimeSpan Duration => End - Start;

    public string Summary()
    {
        var parts = new List<string> { $"GPU avg {GpuAvg:0}%" };
        if (HotspotMax is { } h) parts.Add($"hotspot max {h:0}°C");
        else if (GpuTempMax is { } g) parts.Add($"GPU max {g:0}°C");
        if (CpuTempMax is { } c) parts.Add($"CPU max {c:0}°C");
        if (ParkedPct is { } p) parts.Add($"CCD1 parked {p:0}% of the time");
        parts.Add($"VRAM peak {VramPeakGb:0.0} GB");
        return string.Join(", ", parts) + ".";
    }
}

/// <summary>Follows one game session at a time and summarises it when the game closes.</summary>
public sealed class GameTracker
{
    private const int EndAfterMissingSamples = 10;

    private string? _game;
    private DateTime _start, _last;
    private int _samples, _missing, _ccdKnown, _parked, _bothActiveStreak;
    private double _gpuSum, _vramPeak;
    private double? _gpuTempMax, _hotspotMax, _cpuTempMax;

    public string? CurrentGame => _game;
    public DateTime? Started => _game is null ? null : _start;

    /// <summary>True when the X3D driver has left both CCDs active through a minute of real GPU load.</summary>
    public bool BothCcdsActiveWarning => _bothActiveStreak >= 60;

    public GameSession? Update(Snapshot s)
    {
        var game = s.Game;
        if (game is null)
        {
            if (_game is null) return null;
            if (++_missing < EndAfterMissingSamples) return null;
            return Finish();
        }

        GameSession? finished = null;
        if (_game is not null && _game != game.Label) finished = Finish();
        if (_game is null)
        {
            _game = game.Label;
            _start = s.Time;
        }

        _missing = 0;
        _last = s.Time;
        _samples++;
        _gpuSum += s.GpuLoad;
        _vramPeak = Math.Max(_vramPeak, s.VramUsedGb);
        _gpuTempMax = Max(_gpuTempMax, s.GpuTemp);
        _hotspotMax = Max(_hotspotMax, s.GpuHotspot);
        _cpuTempMax = Max(_cpuTempMax, s.CpuTemp);
        if (s.Ccd != CcdState.Unknown)
        {
            _ccdKnown++;
            if (s.Ccd == CcdState.OtherParked) _parked++;
        }
        _bothActiveStreak = s.Ccd == CcdState.BothActive && s.GpuLoad >= 50 ? _bothActiveStreak + 1 : 0;
        return finished;
    }

    private GameSession Finish()
    {
        var session = new GameSession(_game!, _start, _last, _samples == 0 ? 0 : _gpuSum / _samples,
            _gpuTempMax, _hotspotMax, _cpuTempMax, _ccdKnown == 0 ? null : 100.0 * _parked / _ccdKnown, _vramPeak);
        _game = null;
        _samples = _missing = _ccdKnown = _parked = _bothActiveStreak = 0;
        _gpuSum = _vramPeak = 0;
        _gpuTempMax = _hotspotMax = _cpuTempMax = null;
        return session;
    }

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
