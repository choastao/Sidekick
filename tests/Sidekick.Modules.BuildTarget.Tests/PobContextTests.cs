using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 评估场景（BUILD / MAP / BOSS）这条链的守门员：
///
///   1. **缓存键必须带场景** —— 切了场景还命中旧缓存 = 拿旧场景的基线对新场景的候选做差，
///      面板上的差值全是错的而且看不出来（比「引擎报错」坏得多，因为它看起来是对的）；
///   2. **只认 BUILD / MAP / BOSS** —— 认不出的值（用户手改坏 JSON、将来的新标签）一律按 BD 原样算，
///      绝不把一个我们自己都不认识的标签发给引擎；
///   3. **下标越界**：应答里没回 context 的老 helper 与「回了别的场景」要在解析层就能区分。
/// </summary>
public class PobContextTests
{
    private static PobEngineResponse Decode(string line) =>
        PobEngineProtocol.Decode(line) ?? throw new InvalidOperationException("decode 返回 null");

    // ---- 缓存键 ----

    [Fact]
    public void 切场景必须让基线缓存失效()
    {
        var cache = new BaselineCache();
        var stats = new PobStats(7_644_241, 23_554, 2_433);

        cache.Store("template-a", engineGeneration: 1, PobContexts.Build, stats);
        Assert.True(cache.IsValid("template-a", 1, PobContexts.Build));

        // ⚠ 这条就是本次要钉住的判据：同一个模板、同一个引擎代次，只是换了场景
        //   → 引擎里那份 BD 已经不是这个场景的配置了，必须重载。
        Assert.False(cache.IsValid("template-a", 1, PobContexts.Map));
        Assert.False(cache.IsValid("template-a", 1, PobContexts.Boss));

        // 场景一致（大小写不同）仍算命中：协议里都是大写，别因为大小写多载一次
        Assert.True(cache.IsValid("template-a", 1, "build"));

        cache.Store("template-a", 1, PobContexts.Boss, stats);
        Assert.True(cache.IsValid("template-a", 1, PobContexts.Boss));
        Assert.False(cache.IsValid("template-a", 1, PobContexts.Build));
    }

    // ---- 标签归范 ----

    [Theory]
    [InlineData("BUILD", PobContexts.Build)]
    [InlineData("MAP", PobContexts.Map)]
    [InlineData("BOSS", PobContexts.Boss)]
    [InlineData("map", PobContexts.Map)]      // 大小写容错
    [InlineData("  boss  ", PobContexts.Boss)] // 首尾空白容错
    [InlineData("", PobContexts.Build)]
    [InlineData(null, PobContexts.Build)]
    [InlineData("PINNACLE", PobContexts.Build)] // 认不出的一律按 BD 原样
    public void 只认三个标签_其余当_BUILD(string? value, string expected)
    {
        Assert.Equal(expected, PobContexts.Normalize(value));
    }

    [Theory]
    [InlineData(PobContexts.Build, "Context_Build")]
    [InlineData(PobContexts.Map, "Context_Map")]
    [InlineData(PobContexts.Boss, "Context_Boss")]
    [InlineData("nonsense", "Context_Build")]
    public void 场景对应到界面文案键(string value, string expected)
    {
        Assert.Equal(expected, PobContexts.ResourceKey(value));
    }

    // ---- 设置项 ----

    [Fact]
    public void 设置项默认是_BUILD_并原样落盘()
    {
        var path = Path.Combine(Path.GetTempPath(), "tao-options-context-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new BuildTargetOptionsStore(NullLogger<BuildTargetOptionsStore>.Instance, path);

        try
        {
            Assert.Equal(PobContexts.Build, store.PobContext);

            store.SetPobContext(PobContexts.Boss);
            Assert.Equal(PobContexts.Boss, store.PobContext);

            // JSON 键名要与其它开关同一套（camelCase），测试用真文件读回来
            var json = File.ReadAllText(path);
            Assert.Contains("\"pobContext\": \"BOSS\"", json);

            var reloaded = new BuildTargetOptionsStore(NullLogger<BuildTargetOptionsStore>.Instance, path);
            Assert.Equal(PobContexts.Boss, reloaded.PobContext);

            // 认不出的值落到文件里也读得回来，但一律当 BUILD
            store.SetPobContext("SomethingNew");
            Assert.Equal(PobContexts.Build, store.PobContext);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 老开关文件没有这一项时是_BUILD()
    {
        var path = Path.Combine(Path.GetTempPath(), "tao-options-old-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"craftCost":true,"pobEngine":true}""");

        var store = new BuildTargetOptionsStore(NullLogger<BuildTargetOptionsStore>.Instance, path);

        try
        {
            Assert.Equal(PobContexts.Build, store.PobContext);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 开关文件里是坏值时按_BUILD_算()
    {
        var path = Path.Combine(Path.GetTempPath(), "tao-options-bad-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"pobContext":"夜图"}""");

        var store = new BuildTargetOptionsStore(NullLogger<BuildTargetOptionsStore>.Instance, path);

        try
        {
            Assert.Equal(PobContexts.Build, store.PobContext);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 场景字段随选项一起序列化()
    {
        var options = new BuildTargetOptions { PobContext = PobContexts.Map };
        var json = JsonSerializer.Serialize(options, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Contains("\"pobContext\":\"MAP\"", json);

        var back = JsonSerializer.Deserialize<BuildTargetOptions>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal(PobContexts.Map, back!.PobContext);
    }

    // ---- helper 应答里的实际生效标签 ----

    [Fact]
    public void 读出引擎回的实际生效场景()
    {
        var response = Decode("""{"id":1,"ok":true,"result":{"context":"MAP","stats":{"dps":1,"ehp":2,"life":3}}}""");

        Assert.Equal("MAP", PobCompareService.ReadContext(response));
    }

    [Fact]
    public void 老_helper_没回这一项时是_null()
    {
        // null = 「它没告诉我们」，与「它回了别的场景」是两件事（后者要报错，前者只告警）
        var response = Decode("""{"id":1,"ok":true,"result":{"stats":{"dps":1,"ehp":2,"life":3}}}""");

        Assert.Null(PobCompareService.ReadContext(response));
    }

    [Fact]
    public void 形状不对的场景字段不算数()
    {
        Assert.Null(PobCompareService.ReadContext(Decode("""{"id":1,"ok":true,"result":{"context":84}}""")));
        Assert.Null(PobCompareService.ReadContext(Decode("""{"id":1,"ok":true,"result":{"context":null}}""")));
        Assert.Null(PobCompareService.ReadContext(null));
    }

    // ---- S1：引擎**没确认**场景时不许照抄设置里的标签 ----
    //
    // 病根：老 helper 不回 context 时只写一条 log，界面照样显示「评估场景：打王」，
    // 而数值其实是按 BD 原样算的 —— 与「回了别的场景」那种硬失败是两种态度。
    // 口径：**引擎没确认，界面就不许说「打王」**（数值带标志位，面板显示「场景未生效」）。

    [Theory]
    [InlineData("BOSS", "BOSS", true)]     // 回了、且一致 → 确认
    [InlineData("boss", "BOSS", true)]     // 大小写 / 空白容错（协议里都是大写，别因为大小写就说不确认）
    [InlineData("  map  ", "MAP", true)]
    [InlineData(null, "BOSS", false)]      // ⚠ 老 helper 没回：**没确认**（数值是 BD 原样）
    [InlineData(null, "MAP", false)]
    [InlineData("MAP", "MAP", true)]
    public void 引擎有没有确认请求的场景(string? effective, string requested, bool expected)
    {
        Assert.Equal(expected, PobCompareService.ContextConfirmedByHelper(effective, requested));
    }

    [Fact]
    public void BUILD_请求不需要_helper_确认()
    {
        // BUILD = 「按 BD 自己的配置算」，helper 有没有这个字段，这个语义都成立 —— 不该去报警。
        Assert.True(PobCompareService.ContextConfirmedByHelper(null, PobContexts.Build));
        Assert.True(PobCompareService.ContextConfirmedByHelper(PobContexts.Build, PobContexts.Build));

        // 用户手改坏的值一律当 BUILD（见 PobContexts.Normalize），所以同样是「不需要确认」
        Assert.True(PobCompareService.ContextConfirmedByHelper(null, "夜图"));
    }

    [Fact]
    public void 没确认的场景在数值上留下了标志()
    {
        // 老 helper + 打王请求：数值是 BD 原样，标志位必须为真（界面据此显示「场景未生效」）
        var unconfirmed = new PobStats(1, 2, 3) with
        {
            RequestedContext = PobContexts.Boss,
            ContextConfirmed = PobCompareService.ContextConfirmedByHelper(null, PobContexts.Boss),
        };

        Assert.True(unconfirmed.ContextUnconfirmed);

        // 新版 helper（回了 context）→ 标志位为假，界面照旧显示「打王」
        var confirmed = unconfirmed with
        {
            ContextConfirmed = PobCompareService.ContextConfirmedByHelper(PobContexts.Boss, PobContexts.Boss),
        };

        Assert.False(confirmed.ContextUnconfirmed);

        // BUILD（含手搓的 stats：RequestedContext 为 null）不算「未确认」—— 绝大多数路径都在这边
        Assert.False(new PobStats(1, 2, 3).ContextUnconfirmed);
    }

    [Fact]
    public void 抗性六项缺任何一项都算没报全_混沌抗不算()
    {
        // 全有 → 守门生效，界面不出提示
        var complete = new PobStats(1, 2, 3)
        {
            FireResist = 75,
            ColdResist = 75,
            LightningResist = 75,
            ChaosResist = 0,
            FireResistOver = 10,
            ColdResistOver = 0,
            LightningResistOver = 0,
        };

        Assert.True(complete.ResistanceDataComplete);
        Assert.False(complete.ResistanceDataIncomplete);

        // 审计 S3：守门只读六项（火/冰/电 的 Resist 与 ResistOver），**混沌抗不参与**（上限另算）。
        // 只缺混沌抗时必须仍算「报全」—— 否则界面会把「逐元素」的守门说成「全局没生效」。
        var chaosMissing = complete with { ChaosResist = null };

        Assert.True(chaosMissing.ResistanceDataComplete);
        Assert.False(chaosMissing.ResistanceDataIncomplete);

        // 老 helper：只报了有效抗性、没报溢出 → 守门看不出「顶到上限」，界面必须出提示
        var partial = complete with { FireResistOver = null };

        Assert.False(partial.ResistanceDataComplete);
        Assert.True(partial.ResistanceDataIncomplete);

        // 一个都没报（最老的那份 helper）同理
        Assert.True(new PobStats(1, 2, 3).ResistanceDataIncomplete);
    }

    // ---- 回退链要用的两个字段（helper → 主程序） ----

    [Fact]
    public void 数值解析带上_CombinedDPS_与_FullDPS()
    {
        var response = Decode("""
            {"id":1,"ok":true,"result":{"stats":{"dps":0,"ehp":23554,"life":2433,"combinedDps":7649175,"fullDps":0}}}
            """);

        var stats = PobCompareService.ReadStats(response);

        Assert.NotNull(stats);
        Assert.Equal(0, stats!.Dps);
        Assert.Equal(7_649_175, stats.CombinedDps);
        Assert.Equal(0, stats.FullDps);

        // 回退链直接在读回来的这份上生效
        var metric = PobPrimaryMetric.Select(stats);
        Assert.Equal(PobPrimaryMetric.CombinedDps, metric.Key);
    }

    [Fact]
    public void 老_helper_缺这两个字段时给_0_而不是整份为_null()
    {
        var response = Decode("""{"id":1,"ok":true,"result":{"stats":{"dps":1,"ehp":2,"life":3}}}""");

        var stats = PobCompareService.ReadStats(response);

        Assert.NotNull(stats);
        Assert.Equal(0, stats!.CombinedDps);
        Assert.Equal(0, stats.FullDps);
    }
}
