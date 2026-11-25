using EasyTierExt.Contracts.Models;
using Xunit;

namespace EasyTierExt.Tests;

public class MenuNamingValidatorTests
{
    [Fact]
    public void Valid_PluginNameDashMenuName_Passes()
    {
        var manifest = new PluginManifest
        {
            Id = "screen-share",
            Name = "屏幕共享",
            Entry = "ScreenShare.exe",
            Menus =
            [
                new PluginMenuSpec { Id = "settings", Title = "屏幕共享 - 节点与设置" },
                new PluginMenuSpec { Id = "viewer", Title = "屏幕共享 - 远程画面" }
            ]
        };

        var (ok, err) = MenuNamingValidator.Validate(manifest);
        Assert.True(ok, err);
    }

    [Theory]
    [InlineData("节点与设置")]
    [InlineData("其他插件 - 节点与设置")]
    [InlineData("屏幕共享 - ")]
    public void Invalid_MenuTitle_Fails(string badTitle)
    {
        var manifest = new PluginManifest
        {
            Id = "screen-share",
            Name = "屏幕共享",
            Entry = "ScreenShare.exe",
            Menus = [new PluginMenuSpec { Id = "m1", Title = badTitle }]
        };

        var (ok, _) = MenuNamingValidator.Validate(manifest);
        Assert.False(ok);
    }
}
