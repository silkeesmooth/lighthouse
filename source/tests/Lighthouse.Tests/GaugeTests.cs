using Lighthouse.Core;
using Lighthouse.Core.Model;
using Lighthouse.Core.Sensors;

namespace Lighthouse.Tests;

public class GaugeTests
{
    private static readonly Snapshot Working = new()
    {
        CpuLoad = 40, CpuTemp = 61, CpuPower = 90, CpuClockMhz = 5100, FullSensors = true,
        GpuLoad = 97, GpuTemp = 74, GpuHotspot = 88, GpuPower = 300,
        RamUsedGb = 32, RamTotalGb = 64, CommitPct = 55, VramUsedGb = 10, VramTotalGb = 20,
    };

    private static readonly DisplayAdapter Stopped = new("AMD Radeon RX 7900 XT", 31);

    [Fact]
    public void Working_gauges_show_their_readings()
    {
        Assert.Equal(new GaugeFace(0.4, "40%", "61°C", "90 W · 5.1 GHz", "Accent"), Gauges.Cpu(Working));
        Assert.Equal("97%", Gauges.Gpu(Working).Ring);
        Assert.Equal("74°C", Gauges.Gpu(Working).Main);
        Assert.Equal(new GaugeFace(0.5, "50%", "32.0 GB", "of 64 GB · commit 55%", "Accent"), Gauges.Ram(Working));
        Assert.Equal(new GaugeFace(0.5, "50%", "10.0 GB", "of 20 GB", "Accent"), Gauges.Vram(Working));
    }

    [Fact]
    public void A_stopped_gpu_driver_reads_as_stopped_on_gpu_and_vram_not_as_zero()
    {
        var s = Working with { GpuStopped = Stopped, GpuLoad = 0, GpuTemp = null, GpuHotspot = null, GpuPower = null, VramUsedGb = 0 };
        foreach (var face in new[] { Gauges.Gpu(s), Gauges.Vram(s) })
        {
            Assert.Equal("Stopped", face.Main);
            Assert.Equal("GPU driver, Device Manager code 31", face.Detail);
            Assert.Equal("—", face.Ring);
            Assert.Equal("Warn", face.Accent);
        }
        Assert.Equal("GPU driver stopped", Gauges.GpuLine(s));
        Assert.StartsWith("The AMD Radeon RX 7900 XT driver stopped (Device Manager code 31)", Headline.Build(s, [], DateTime.Now));
    }

    [Fact]
    public void A_missing_temperature_says_so_and_why()
    {
        var s = Working with { CpuTemp = null, CpuPower = null, CpuClockMhz = null, FullSensors = false,
            SensorError = "the sensor helper isn't running", GpuTemp = null };
        Assert.Equal("No temp", Gauges.Cpu(s).Main);
        Assert.Equal("sensor helper not running", Gauges.Cpu(s).Detail);
        Assert.Equal("40%", Gauges.Cpu(s).Ring); // the load is still read
        Assert.Equal("No temp", Gauges.Gpu(s).Main);
        Assert.Equal("sensors unavailable", Gauges.Cpu(s with { SensorError = "the sensor helper isn't running as admin" }).Detail);
    }

    [Fact]
    public void Load_windows_gave_no_figure_for_is_not_shown_as_zero()
    {
        var s = Working with { CpuLoadRead = false, CpuLoad = 0, GpuLoadRead = false, GpuLoad = 0, RamTotalGb = 0 };
        Assert.Equal("—", Gauges.Cpu(s).Ring);
        Assert.StartsWith("no load reading", Gauges.Cpu(s).Detail);
        Assert.Equal("—", Gauges.Gpu(s).Ring);
        Assert.Equal("74°C", Gauges.Gpu(s).Main);
        Assert.Equal("No reading", Gauges.Vram(s).Main);
        Assert.Equal(new GaugeFace(0, "—", "No reading", "Windows gave no figure", "Muted") { Short = "No data" }, Gauges.Ram(s));
        Assert.Equal("GPU · 74°C", Gauges.GpuLine(s));
    }

    [Fact]
    public void A_stopped_graphics_card_counts_when_it_is_the_one_read_or_built_in_graphics_stands_in()
    {
        var card = new GpuAdapter("AMD Radeon RX 7900 XT", 20, 1, 0);
        var builtIn = new GpuAdapter("AMD Radeon(TM) Graphics", 2.0, 2, 0);
        DisplayAdapter dead = Stopped, fine = new("AMD Radeon(TM) Graphics", 0);

        Assert.Equal(dead, DisplayAdapters.Stopped([dead, fine], card)); // died while Lighthouse read it
        Assert.Equal(dead, DisplayAdapters.Stopped([dead, fine], builtIn)); // already dead when Lighthouse started
        Assert.Equal(dead, DisplayAdapters.Stopped([dead], null));
        Assert.Null(DisplayAdapters.Stopped([new("AMD Radeon RX 7900 XT", 0), fine], card));
        // The built-in GPU failing next to a working card is not the GPU gauge's business.
        Assert.Null(DisplayAdapters.Stopped([new("AMD Radeon RX 7900 XT", 0), new("AMD Radeon(TM) Graphics", 43)], card));
        // Switched off in Device Manager (code 22) is a choice, not a stopped driver.
        Assert.Null(DisplayAdapters.Stopped([new("AMD Radeon RX 7900 XT", DisplayAdapter.DisabledByUser)], builtIn));
    }
}
