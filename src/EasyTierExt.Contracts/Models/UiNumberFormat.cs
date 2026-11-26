using System.Globalization;

namespace EasyTierExt.Contracts.Models;

/// <summary>主程序与插件共用的数字显示规则：10 及以上显示整数，10 以下保留一位小数。</summary>
public static class UiNumberFormat
{
    public static string Number(double value)
    {
        double oneDecimal = Math.Round(value, 1);
        return Math.Abs(oneDecimal) >= 10
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : oneDecimal.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>按进制 k 换算到合适单位（单位数组从小到大），再按 <see cref="Number"/> 规则显示。第一个单位显示整数。</summary>
    public static string Scaled(double value, double k, IReadOnlyList<string> units)
    {
        int i = 0;
        while (Math.Round(value) >= k && i < units.Count - 1)
        {
            value /= k;
            i++;
        }
        return i == 0
            ? $"{Math.Round(value).ToString("0", CultureInfo.InvariantCulture)} {units[0]}"
            : $"{Number(value)} {units[i]}";
    }
}
