using EasyTierExt.Contracts.Models;
using Xunit;

namespace EasyTierExt.Tests;

public class UiNumberFormatTests
{
    [Theory]
    [InlineData(76.81, "77")]
    [InlineData(39.2, "39")]
    [InlineData(10.0, "10")]
    [InlineData(9.96, "10")]   // 四舍五入后进位到两位数，按整数显示
    [InlineData(9.94, "9.9")]
    [InlineData(1.0, "1.0")]
    [InlineData(0.04, "0.0")]
    [InlineData(123.4, "123")]
    public void Number_IntegerFromTens_OneDecimalBelow(double value, string expected)
    {
        Assert.Equal(expected, UiNumberFormat.Number(value));
    }

    [Theory]
    [InlineData(39_200_000, "39 Mbps")]
    [InlineData(61_100_000, "61 Mbps")]
    [InlineData(5_430_000, "5.4 Mbps")]
    [InlineData(999_700_000, "1.0 Gbps")]  // 999.7 Mbps 取整后满 1000，换到上一级单位
    [InlineData(820, "820 bps")]
    public void Scaled_Bitrate(double bps, string expected)
    {
        Assert.Equal(expected, UiNumberFormat.Scaled(bps, 1000, ["bps", "Kbps", "Mbps", "Gbps"]));
    }

    [Theory]
    [InlineData(80_540_000, "77 MB")]
    [InlineData(51_380_224, "49 MB")]
    [InlineData(3_355_443, "3.2 MB")]
    [InlineData(512, "512 B")]
    public void Scaled_Bytes(double bytes, string expected)
    {
        Assert.Equal(expected, UiNumberFormat.Scaled(bytes, 1024, ["B", "KB", "MB", "GB", "TB"]));
    }
}
