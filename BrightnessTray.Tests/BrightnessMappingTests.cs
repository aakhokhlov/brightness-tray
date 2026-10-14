using BrightnessTray.Services;
using Xunit;

namespace BrightnessTray.Tests;

public class BrightnessMappingTests
{
    [Fact]
    public void FullRangeMapsIdentity()
    {
        Assert.Equal(0, MonitorItem.PercentToRaw(0, 0, 100));
        Assert.Equal(50, MonitorItem.PercentToRaw(50, 0, 100));
        Assert.Equal(100, MonitorItem.PercentToRaw(100, 0, 100));
    }

    [Fact]
    public void RestrictedRangeClampsToRawBounds()
    {
        // A display whose raw DDC range is 30..80.
        Assert.Equal(30, MonitorItem.PercentToRaw(0, 30, 80));
        Assert.Equal(80, MonitorItem.PercentToRaw(100, 30, 80));
        Assert.Equal(55, MonitorItem.PercentToRaw(50, 30, 80));
        Assert.Equal(30, MonitorItem.PercentToRaw(-20, 30, 80));
        Assert.Equal(80, MonitorItem.PercentToRaw(140, 30, 80));
    }

    [Fact]
    public void EmptyRangeReadsAsNull()
    {
        Assert.Null(MonitorItem.RawToPercent(10, min: 40, max: 40));
        Assert.Null(MonitorItem.RawToPercent(10, min: 50, max: 40));
    }

    [Fact]
    public void RawToPercentStaysIn0to100()
    {
        Assert.Equal(0, MonitorItem.RawToPercent(30, 30, 80));
        Assert.Equal(100, MonitorItem.RawToPercent(80, 30, 80));
        Assert.Equal(50, MonitorItem.RawToPercent(55, 30, 80));
    }
}
