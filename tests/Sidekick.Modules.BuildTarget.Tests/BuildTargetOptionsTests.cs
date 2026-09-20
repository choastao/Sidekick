using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// UI 契约：开关拨动时订阅方会被叫到，**而且叫到的时候读到的是新值**。
    ///
    /// 为什么单独钉一条：评估面板（<c>PobComparePanel</c> / <c>CraftCostPanel</c>）就是靠
    /// 这个回调决定「收干净 / 立刻重算」的，而它们进入回调的第一件事是**再读一次这个 store**。
    /// 所以「事件在改值之后触发」不是一个实现细节，是订阅方能拿到正确前提的必要条件 ——
    /// 把 <c>Set</c> 里的顺序反过来（先 Invoke 再 change），面板会按**旧**开关值评估，
    /// 而那种错在界面上的表现只是「拨开之后面板还是空的」，看日志也看不出来。
    /// </summary>
    [Fact]
    public void 开关变化会通知订阅方且订阅方读到的是新值()
    {
        var path = Path.Combine(Path.GetTempPath(), "tao-options-test-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new BuildTargetOptionsStore(NullLogger<BuildTargetOptionsStore>.Instance, path);

        var seen = new List<bool>();
        store.OnChanged += () => seen.Add(store.CraftCost);

        store.SetCraftCost(true);
        store.SetCraftCost(false);

        Assert.Equal([true, false], seen);

        // 顺带确认落盘的是新值（订阅方之外还有人读这个文件）
        var reloaded = new BuildTargetOptionsStore(NullLogger<BuildTargetOptionsStore>.Instance, path);
        Assert.False(reloaded.CraftCost);

        File.Delete(path);
    }
}
