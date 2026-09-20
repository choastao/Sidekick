using System.Text.Json;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 功能开关的约定：**两项默认都是关的**。
/// 这条测试是用来钉住这个约定的 —— 谁把默认值改成开，这里就会红，
/// 于是「关着的功能不读数据文件」这个前提不会被悄悄破坏。
/// </summary>
public class BuildTargetOptionsTests
{
    [Fact]
    public void Defaults_are_off()
    {
        var options = new BuildTargetOptions();

        Assert.False(options.CraftCost);
        Assert.False(options.PobEngine);
    }

    [Fact]
    public void Missing_fields_deserialize_to_off()
    {
        // 老文件里没有新开关（或用户手写了一个空对象）时必须当成"关"，不能抛。
        var options = JsonSerializer.Deserialize<BuildTargetOptions>(
            "{}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(options);
        Assert.False(options!.CraftCost);
        Assert.False(options.PobEngine);
    }

    /// <summary>
    /// 功能开关拨动时「该不该停引擎」的方向：**只有关掉才停，打开绝不停**。
    /// 反过来（开着就把引擎杀掉）会让功能看起来随机失灵，而且因为引擎是懒启动的，
    /// 错误方向在单测里看不出来 —— 所以单独钉住。
    /// </summary>
    [Theory]
    [InlineData(true, true, false)]   // 打开 + 在跑 → 不停（引擎要留着用）
    [InlineData(true, false, false)]  // 打开 + 没跑 → 无事可做（懒启动，不在这里起）
    [InlineData(false, true, true)]   // 关掉 + 在跑 → 停（真的卸载，不是「不再调用」）
    [InlineData(false, false, false)] // 关掉 + 没跑 → 空操作
    public void Only_switching_off_stops_the_engine(bool enabled, bool running, bool expected)
    {
        Assert.Equal(expected, PobEngineClient.ShouldStopForOptions(enabled, running));
    }

    [Fact]
    public void Roundtrips_camel_case_booleans()
    {
        var options = new BuildTargetOptions { CraftCost = true, PobEngine = true };
        var json = JsonSerializer.Serialize(options, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        Assert.Contains("\"craftCost\":true", json);
        Assert.Contains("\"pobEngine\":true", json);

        var back = JsonSerializer.Deserialize<BuildTargetOptions>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.True(back!.CraftCost);
        Assert.True(back.PobEngine);
    }
}
