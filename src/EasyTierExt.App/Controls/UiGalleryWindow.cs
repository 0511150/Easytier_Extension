using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using EasyTierExt.App.Native;
using EasyTierExt.Contracts.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EasyTierExt.App.Controls;

/// <summary>
/// 控件预览窗口（EasyTierClient.exe --ui-gallery）：用示例 Schema 渲染全部声明式控件，
/// 供插件开发者查看效果，也用于检查各种窗口宽度下的布局。不加载配置、不启动网络与插件。
/// </summary>
public sealed class UiGalleryWindow : Window
{
    private const string PluginId = "ui-gallery";
    private const string MenuId = "main";

    private readonly DeclarativePluginRenderer _renderer;

    public UiGalleryWindow()
    {
        Title = "EasyTier with Extension - 控件预览";
        var host = new Grid { Padding = new Thickness(20), Background = new SolidColorBrush(Color.FromArgb(255, 20, 20, 22)) };
        Content = host;

        var viewport = new ViewportHostController(WinRT.Interop.WindowNative.GetWindowHandle(this), host, (_, _) => Task.CompletedTask);
        _renderer = new DeclarativePluginRenderer((_, _, action) =>
        {
            var text = $"{action.Key} · {action.Action}" +
                       (action.Value is { } v ? $" · {v.GetRawText()}" : string.Empty) +
                       (action.RowId is { } r ? $" · 行 {r}" : string.Empty);
            _renderer!.ApplyValues(new Dictionary<string, JsonElement> { ["last_action"] = JsonSerializer.SerializeToElement(text) });
            return Task.CompletedTask;
        }, viewport);

        host.Children.Add(_renderer.Render(PluginId, MenuId, BuildSchema(), null));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900));
    }

    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static UiSelectOption O<T>(string label, T value) => new() { Label = label, Value = J(value) };

    private static UiPageSchema BuildSchema()
    {
        var cards = new[] { "Easytier_Extension/插件开发规范.md - Visual Studio Code", "Claude", "Antigravity", "ChatGPT", "CC Switch", "EasyTier with Extension" }
            .Select((t, i) => new UiSourceCard { Id = $"w{i}", Title = t, Subtitle = "进程: demo.exe" })
            .ToList();

        return new UiPageSchema
        {
            Sections =
            [
                new UiSection
                {
                    Id = "toolbar",
                    Title = "紧凑工具栏（compact）",
                    Compact = true,
                    Items =
                    [
                        new UiItem { Key = "last_action", Type = "status", Value = J("最近操作会显示在这里；拖动窗口宽度查看各控件的自适应效果") },
                        new UiItem { Key = "tb_mute", Type = "button", Label = "静音" },
                        new UiItem { Key = "tb_control", Type = "button", Label = "对方未开启被控", Disabled = true },
                        new UiItem { Key = "tb_exit", Type = "button", Label = "断开并返回", Danger = true }
                    ]
                },
                new UiSection
                {
                    Id = "split",
                    Title = "左右分栏（split，窄屏自动上下堆叠）",
                    Layout = "split",
                    Items =
                    [
                        new UiItem
                        {
                            Key = "source", Type = "source_picker", Column = "left", ActiveTab = "window", Value = J("w1"),
                            Tabs = [O("程序", "window"), O("屏幕", "fullscreen")], Cards = cards
                        },
                        new UiItem
                        {
                            Key = "quality", Type = "segmented", Stacked = true, Label = "画质", Subtitle = "同画质下，帧率越低单帧画面越清晰",
                            Value = J(6), Options = [O("2 Mbps", 2), O("4 Mbps", 4), O("6 Mbps", 6)]
                        },
                        new UiItem { Key = "fps", Type = "segmented", Stacked = true, Label = "帧率", Value = J(15), Options = [O("15 FPS", 15), O("30 FPS", 30), O("60 FPS", 60)] },
                        new UiItem { Key = "audio", Type = "switch", Label = "共享声音", Value = J(true) },
                        new UiItem { Key = "remote", Type = "switch", Label = "允许远控", Value = J(true) },
                        new UiItem { Key = "share_status", Type = "status", Label = "当前状态", Value = J("未开启 · 就绪（生效到全部 1 个在线网络）") },
                        new UiItem { Key = "share", Type = "button", Primary = true, Label = "开始共享" }
                    ]
                },
                new UiSection
                {
                    Id = "inputs",
                    Title = "输入控件",
                    Items =
                    [
                        new UiItem { Key = "sw", Type = "switch", Label = "开关 switch", Subtitle = "标签与说明文字会自动换行", Value = J(true) },
                        new UiItem { Key = "sel", Type = "select", Label = "下拉框 select", Value = J("b"), Options = [O("选项 A", "a"), O("较长的选项 B", "b"), O("选项 C", "c")] },
                        new UiItem
                        {
                            Key = "seg", Type = "segmented", Label = "分段按钮 segmented（5 项）", Value = J(3),
                            Options = [O("自动", 0), O("1 线程", 1), O("2 线程", 2), O("4 线程", 3), O("8 线程", 4)]
                        },
                        new UiItem { Key = "inp", Type = "input", Label = "文本框 input", Placeholder = "请输入名称", Value = J("") },
                        new UiItem { Key = "pwd", Type = "password", Label = "密码框 password", Placeholder = "仅本机保存" },
                        new UiItem { Key = "num", Type = "number", Label = "数字框 number", Min = 1, Max = 65535, Value = J(39700) },
                        new UiItem { Key = "sld", Type = "slider", Label = "滑块 slider", Min = 0, Max = 100, Step = 5, Unit = "%", Value = J(60) },
                        new UiItem { Key = "chk", Type = "checkbox", Label = "复选框 checkbox：开机后自动连接", Subtitle = "复选框的说明文字", Value = J(true) },
                        new UiItem { Key = "rad", Type = "radio", Label = "单选 radio", Value = J("udp"), Options = [O("TCP", "tcp"), O("UDP", "udp"), O("QUIC", "quic"), O("WebSocket", "ws")] },
                        new UiItem
                        {
                            Key = "chl", Type = "checklist", Label = "多选 checklist", Value = J(new[] { "lan", "game" }),
                            Options = [O("局域网发现", "lan"), O("游戏加速", "game"), O("文件共享", "file"), O("远程桌面", "rdp"), O("打印机共享", "print")]
                        },
                        new UiItem { Key = "txa", Type = "textarea", Label = "多行文本 textarea", Placeholder = "每行一个地址" },
                        new UiItem { Key = "btn", Type = "button", Label = "普通按钮 button" },
                        new UiItem
                        {
                            Key = "grp", Type = "button_group", Label = "按钮组 button_group",
                            Options = [O("刷新", "refresh"), O("导出记录", "export"), O("复制诊断信息", "copy"), O("打开日志目录", "logs")]
                        },
                        new UiItem { Key = "lnk", Type = "link", Label = "链接 link", Url = "https://github.com/0511150/Easytier_Extension", Value = J("项目主页") },
                        new UiItem { Key = "st", Type = "status", Label = "状态 status", Value = J("这是一段很长的状态文字，用来检查在窄窗口下会不会被裁切：空闲 · 本机测速服务已在 2 个网络开放 · 正被其他节点测速") }
                    ]
                },
                new UiSection
                {
                    Id = "display",
                    Title = "展示控件",
                    Items =
                    [
                        new UiItem { Key = "txt", Type = "text", Label = "说明文字 text", Value = J("text 用于大段说明，会按宽度自动换行，并且可以选中复制。插件可以用它展示使用说明、注意事项或协议条款。") },
                        new UiItem { Key = "al1", Type = "alert", Label = "提示 alert（info）", Severity = "info", Value = J("对方同意后才会开始测速。") },
                        new UiItem { Key = "al2", Type = "alert", Label = "成功（success）", Severity = "success", Value = J("测速完成：上传 95.0 Mbps，下载 88.1 Mbps。") },
                        new UiItem { Key = "al3", Type = "alert", Label = "警告（warning）", Severity = "warning", Value = J("当前为中转链路，速度可能受服务器带宽限制。") },
                        new UiItem { Key = "al4", Type = "alert", Label = "错误（error）", Severity = "error", Value = J("无法监听 TCP 39700：端口已被占用。") },
                        new UiItem { Key = "bdg", Type = "badge", Label = "标签 badge", Severity = "success", Value = J("P2P 直连") },
                        new UiItem { Key = "bdg2", Type = "badge", Label = "标签 badge（warning）", Severity = "warning", Value = J("中转") },
                        new UiItem { Key = "div", Type = "divider" },
                        new UiItem { Key = "prg", Type = "progress", Label = "进度条 progress", Value = J(new { value = 42, text = "42% · 剩余 9 秒" }) },
                        new UiItem { Key = "prg2", Type = "progress", Label = "不确定进度", Subtitle = "value 为空或负数时显示为加载动画" },
                        new UiItem
                        {
                            Key = "kv", Type = "key_value", Label = "键值列表 key_value",
                            Value = J(new[]
                            {
                                new { label = "虚拟 IP", value = "10.126.126.2/24" },
                                new { label = "NAT 类型", value = "端口受限锥形" },
                                new { label = "隧道协议", value = "udp" },
                                new { label = "节点 ID", value = "3125987412" },
                                new { label = "版本", value = "2.6.4-8428a89d" }
                            })
                        },
                        new UiItem
                        {
                            Key = "sts", Type = "stats", Label = "统计卡片 stats",
                            Value = J(new object[]
                            {
                                new { label = "在线节点", value = "12", sub = "较昨日 +3" },
                                new { label = "平均延迟", value = "18.4 ms", severity = "success" },
                                new { label = "丢包率", value = "2.1%", severity = "warning" },
                                new { label = "已发送", value = "150.13 GB" }
                            })
                        },
                        new UiItem { Key = "img", Type = "image", Label = "图片 image", Max = 120, Value = J(Convert.ToBase64String(SamplePng())) }
                    ]
                },
                new UiSection
                {
                    Id = "data",
                    Title = "表格与图表",
                    Items =
                    [
                        new UiItem
                        {
                            Key = "tbl", Type = "table", Label = "表格", Subtitle = "列过多时横向滚动，过长的单元格省略并在悬停时显示全文",
                            Columns =
                            [
                                new() { Key = "net", Label = "所属网络" }, new() { Key = "host", Label = "节点名称" }, new() { Key = "ip", Label = "虚拟 IP" },
                                new() { Key = "route", Label = "链路" }, new() { Key = "lat", Label = "延迟" }, new() { Key = "state", Label = "状态" },
                                new() { Key = "last", Label = "最近结果" }
                            ],
                            RowActions = [new() { Id = "test", Label = "测速" }, new() { Id = "watch", Label = "观看", WhenKey = "sharing", WhenEquals = J(true) }],
                            Rows =
                            [
                                Row("1", "家里的网络", "DESKTOP-WORKSTATION-01", "10.126.126.2", "P2P", "12 ms", "可测速", "↑ 95.0 Mbps · ↓ 88.1 Mbps", true),
                                Row("2", "公司", "laptop", "10.126.126.15", "RELAY", "48 ms", "对方版本过旧", "-", false)
                            ]
                        },
                        new UiItem { Key = "empty_tbl", Type = "table", Label = "空表格", Placeholder = "暂无测速记录", Columns = [new() { Key = "a", Label = "时间" }, new() { Key = "b", Label = "节点" }] },
                        new UiItem { Key = "cht", Type = "chart", Label = "速率曲线 chart", Value = SampleChart() }
                    ]
                }
            ]
        };
    }

    private static Dictionary<string, JsonElement> Row(string id, string net, string host, string ip, string route, string lat, string state, string last, bool sharing) => new()
    {
        ["row_id"] = J(id), ["net"] = J(net), ["host"] = J(host), ["ip"] = J(ip), ["route"] = J(route),
        ["lat"] = J(lat), ["state"] = J(state), ["last"] = J(last), ["sharing"] = J(sharing)
    };

    private static JsonElement SampleChart()
    {
        var rnd = new Random(7);
        double[][] Series(double level) => Enumerable.Range(1, 150)
            .Select(i => new[] { i / 10.0, Math.Max(0, level * Math.Min(1, i / 12.0) * (0.9 + rnd.NextDouble() * 0.2)) })
            .ToArray();
        return J(new
        {
            subtitle = "DESKTOP-WORKSTATION-01（10.126.126.2） · 延迟 12.3 ms · 抖动 0.8 ms",
            x_max = 15, x_unit = "s", y_format = "bps",
            series = new object[]
            {
                new { name = "上传", icon = "↑", color = "#3B82F6", mark = 39.2e6, legend = "39 Mbps", points = Series(39.2e6) },
                new { name = "下载", icon = "↓", color = "#9CA3AF", mark = 61.1e6, legend = "61 Mbps", points = Series(61.1e6) }
            },
            stats = new object[]
            {
                new { label = "已发送", weight = 1, value = "77 MB" },
                new { label = "已接收", weight = 1, value = "49 MB" },
                new { label = "后 5 秒均值", weight = 2, value = "↑ 39 Mbps  ↓ 61 Mbps" }
            }
        });
    }

    /// <summary>生成一张 96×54 的渐变 PNG，作为 image 控件的示例。</summary>
    private static byte[] SamplePng()
    {
        const int w = 96, h = 54;
        var raw = new byte[(w * 3 + 1) * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = y * (w * 3 + 1) + 1 + x * 3;
                raw[o] = (byte)(40 + x * 2);
                raw[o + 1] = (byte)(80 + y * 2);
                raw[o + 2] = 200;
            }
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(png, "IHDR", ihdr);
        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            WriteChunk(png, "IDAT", compressed.ToArray());
        }
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        s.Write(typed);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        s.Write(crc);
    }
}
