using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sidekick.Common;
using Sidekick.Game;
using Sidekick.Game.Languages.Implementations;
using Sidekick.Game.Providers;
using Sidekick.Game.Stats;
using Sidekick.Game.TradeStats;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// C1 审计出来的两条 🔴 的守门员：
///
/// 1. **基线缓存必须把「引擎代际号」当缓存键**（审计 L1）—— 只认模板 id 的话，
///    引擎被超时杀掉之后缓存照样命中 → 跳过 `load_build` → `equip` 回 `no build loaded`
///    → 此后每次试穿都失败且无自愈路径。C2 的批量会因此整批报销。
///
/// 2. **平添词缀要补上行首 `+`**（审计 C1 4.5）—— 英文 trade-stats 的模板不带 `+`，
///    而 PoB 对缺 `+` 的行是**静默忽略**的（实测：EHP 与不换装备逐位相同，Skipped 却为 0）。
///    这里的第一条用**真实数据**断言这个形状，不用构造夹具（审计 C5 批评的正是用构造夹具给假信心）。
/// </summary>
public class EngineLifecycleTests
{
    private readonly DataProvider dataProvider = new(
        Options.Create(new SidekickConfiguration { ApplicationType = SidekickApplicationType.Test }),
        NullLogger<DataProvider>.Instance);

    // ---- L1：缓存键 = (模板 id, 引擎代际号) ----

    [Fact]
    public void Cache_invalidates_when_the_engine_restarts()
    {
        var cache = new BaselineCache();
        var stats = new PobStats(7_644_241, 23_554, 2_433);

        cache.Store("template-a", engineGeneration: 1, stats);
        Assert.True(cache.IsValid("template-a", 1));
        Assert.Same(stats, cache.Stats);

        // ⚠ 这一条就是 L1 的判据：同一个模板、但引擎换了一代（被杀过/重启过）
        //   → 缓存里的数值已经不属于当前引擎，必须重载。
        Assert.False(cache.IsValid("template-a", 2));

        // 换了模板当然也不认
        Assert.False(cache.IsValid("template-b", 1));

        // 载入失败 / 引擎报 no build loaded 之后清缓存
        cache.Invalidate();
        Assert.False(cache.IsValid("template-a", 1));
        Assert.Null(cache.Stats);

        // 「成功但没读到数值」不算有效缓存
        cache.Store("template-a", 3, null);
        Assert.False(cache.IsValid("template-a", 3));
    }

    [Fact]
    public void Killing_the_helper_bumps_the_engine_generation()
    {
        var client = new PobEngineClient(NullLogger<PobEngineClient>.Instance);
        var before = client.Generation;

        // Dispose → StopProcess：进程没了 = 引擎里载入过的 BD 也没了
        client.Dispose();

        Assert.Equal(before + 1, client.Generation);
    }

    [Fact]
    public void No_build_loaded_is_recognised_as_a_reloadable_state()
    {
        // Lua 侧原文：error("no build loaded")（tao-engine-server.lua 的 equip 分支）
        var response = new PobEngineResponse { Id = 1, Ok = false, Error = "no build loaded" };

        Assert.False(response.Ok);
        Assert.Contains("no build loaded", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ---- C1 4.5：真实数据里「定义文本带 +、trade-stats 模板不带 +」这对形状 ----

    [Fact]
    public async Task Real_data_confirms_flat_affixes_need_the_plus_the_template_lacks()
    {
        var definitions = await dataProvider.Read<List<StatDefinition>>(
            GameType.Poe2,
            GameDataType.Stats,
            new GameLanguageEn());

        var templates = await dataProvider.Read<List<TradeStatDefinition>>(
            GameType.Poe2,
            GameDataType.TradeStats,
            new GameLanguageEn());

        Assert.NotNull(definitions);
        Assert.NotNull(templates);

        // 规模锚点：这不是个例，平添型词缀（定义文本以 + 开头）有上千条
        var flatCount = definitions!.Count(x => x.Text?.StartsWith('+') == true);
        Assert.True(flatCount > 800, $"以 + 开头的英文定义只有 {flatCount} 条，数据形状变了？");

        var life = definitions.First(x => x.Text == "+# to maximum Life");

        // 定义侧：带 +，正则里也把 + 写成捕获组外的字面量
        Assert.NotNull(life.TradeIds);
        Assert.NotEmpty(life.TradeIds!);
        Assert.StartsWith("^\\+", life.Pattern?.ToString() ?? string.Empty, StringComparison.Ordinal);

        // 模板侧：同一个 id 的文本模板**没有**行首 +
        var template = templates!.First(x => x.Id == life.TradeIds![0]);
        Assert.Equal("# to maximum Life", template.Text);
        Assert.False(template.Text.StartsWith('+'));
    }
}
