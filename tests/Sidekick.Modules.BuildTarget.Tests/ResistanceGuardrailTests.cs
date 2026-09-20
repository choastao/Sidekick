using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 抗性上限守门的守门员（<see cref="ResistanceGuardrail.Worsened"/> + helper 那七项字段的解析）。
///
/// 两条纪律：
///   1. **只有「已经顶到上限之上」的抗性被拉低才算变糟** —— 没溢出的抗性掉了是正常取舍，
///      交给 DPS / EHP 那条轴去说；
///   2. **数据缺失 = 不知道**（一律 false）。⚠ 老 helper 不报这几个字段时，
///      「溢出 = 0」会让这条守门整个失效，而看起来又像「检查过了，没问题」——
///      所以缺字段读成 null 而不是 0，这一条必须钉住。
/// </summary>
public class ResistanceGuardrailTests
{
    private static PobStats Stats(
        double dps = 1000,
        double ehp = 1000,
        double? fireRes = null,
        double? fireOverCap = null,
        double? coldRes = null,
        double? coldOverCap = null,
        double? lightningRes = null,
        double? lightningOverCap = null,
        double? chaosRes = null) => new(dps, ehp, 500)
    {
        FireResist = fireRes,
        FireResistOver = fireOverCap,
        ColdResist = coldRes,
        ColdResistOver = coldOverCap,
        LightningResist = lightningRes,
        LightningResistOver = lightningOverCap,
        ChaosResist = chaosRes,
    };

    [Fact]
    public void 顶到上限的抗性被拉低_算缺口变糟()
    {
        var current = Stats(fireRes: 80, fireOverCap: 5);
        var candidate = Stats(fireRes: 76, fireOverCap: 1);

        Assert.True(ResistanceGuardrail.Worsened(current, candidate));
    }

    [Fact]
    public void 抗性没降就没事()
    {
        var current = Stats(fireRes: 80, fireOverCap: 5);

        Assert.False(ResistanceGuardrail.Worsened(current, Stats(fireRes: 80, fireOverCap: 5)));
        Assert.False(ResistanceGuardrail.Worsened(current, Stats(fireRes: 81, fireOverCap: 6)));
    }

    [Fact]
    public void 没顶到上限的抗性掉了_不算变糟()
    {
        // OverCap = 0 = 还没到上限：掉了只是取舍，不由这条守门判。
        var current = Stats(fireRes: 70, fireOverCap: 0);
        var candidate = Stats(fireRes: 60, fireOverCap: 0);

        Assert.False(ResistanceGuardrail.Worsened(current, candidate));
    }

    [Fact]
    public void 冰抗与电抗同样守门()
    {
        Assert.True(ResistanceGuardrail.Worsened(
            Stats(coldRes: 79, coldOverCap: 4),
            Stats(coldRes: 70, coldOverCap: 0)));

        Assert.True(ResistanceGuardrail.Worsened(
            Stats(lightningRes: 90, lightningOverCap: 15),
            Stats(lightningRes: 89, lightningOverCap: 14)));

        Assert.False(ResistanceGuardrail.Worsened(
            Stats(lightningRes: 90, lightningOverCap: 15),
            Stats(lightningRes: 92, lightningOverCap: 17)));
    }

    [Fact]
    public void 混沌抗不参与守门_混沌抗上限是另一回事()
    {
        var current = Stats(chaosRes: 30);
        var candidate = Stats(chaosRes: 0);

        Assert.False(ResistanceGuardrail.Worsened(current, candidate));
    }

    // ---- 数据缺失：一律 false（不知道就不猜） ----

    [Fact]
    public void 任一侧整份为_null_一律_false()
    {
        Assert.False(ResistanceGuardrail.Worsened(null, Stats(fireRes: 76, fireOverCap: 1)));
        Assert.False(ResistanceGuardrail.Worsened(Stats(fireRes: 80, fireOverCap: 5), null));
        Assert.False(ResistanceGuardrail.Worsened(null, null));
    }

    [Fact]
    public void 拿不到溢出时_按默认上限75判到顶_没到上限才算正常取舍()
    {
        // 没有溢出数据（老 helper）时**不拿 0 顶替**，但要按「默认元素抗上限 75 - 0.5」这条兜底判「到顶」：
        //   ① 当前 80（≥74.5）→ 算到顶 + 候选 60 更低 → 缺口变糟 = true；
        //   ② 当前 60（没到上限）→ 抗性掉了是正常取舍 → false（交给 DPS/EHP 那条轴去说）。
        Assert.True(ResistanceGuardrail.Worsened(Stats(fireRes: 80), Stats(fireRes: 60)));
        Assert.False(ResistanceGuardrail.Worsened(Stats(fireRes: 60), Stats(fireRes: 40)));
    }

    [Fact]
    public void 有效抗性任一侧拿不到_一律_false()
    {
        Assert.False(ResistanceGuardrail.Worsened(Stats(fireRes: 80, fireOverCap: 5), Stats(fireOverCap: 1)));
        Assert.False(ResistanceGuardrail.Worsened(Stats(fireOverCap: 5), Stats(fireRes: 60, fireOverCap: 1)));
    }

    [Fact]
    public void 判据只看当前的溢出_候选那侧的溢出值用不到()
    {
        // 规格的条件是「**当前**该项有溢出 且 候选的有效抗性更低」——
        // 候选那侧的 OverCap 拿不到（老 helper）不影响判定：有效抗性是真的降了。
        Assert.True(ResistanceGuardrail.Worsened(
            Stats(fireRes: 80, fireOverCap: 5),
            Stats(fireRes: 60)));
    }

    [Fact]
    public void 老_helper_的整份数值里这些字段都是_null()
    {
        // 关键：默认是 null（不知道），**不是 0**（「这项抗性是 0」）。
        var stats = new PobStats(1000, 2000, 500);

        Assert.Null(stats.FireResist);
        Assert.Null(stats.ColdResist);
        Assert.Null(stats.LightningResist);
        Assert.Null(stats.ChaosResist);
        Assert.Null(stats.FireResistOver);
        Assert.Null(stats.ColdResistOver);
        Assert.Null(stats.LightningResistOver);

        // 老 helper 的数值 → 守门当「不知道」，不参与判定
        Assert.False(ResistanceGuardrail.Worsened(
            stats,
            new PobStats(1000, 2000, 500)));
    }

    // ---- helper → 主程序：这七项读回来的形状 ----

    [Fact]
    public void 数值解析带上七项抗性字段()
    {
        var response = PobEngineProtocol.Decode("""
            {"id":1,"ok":true,"result":{"stats":{"dps":1,"ehp":2,"life":3,
             "fireResist":80,"coldResist":79,"lightningResist":76,"chaosResist":-30,
             "fireResistOver":5,"coldResistOver":4,"lightningResistOver":1}}}
            """)!;

        var stats = PobCompareService.ReadStats(response);

        Assert.NotNull(stats);
        Assert.Equal(80d, stats!.FireResist);
        Assert.Equal(79d, stats.ColdResist);
        Assert.Equal(76d, stats.LightningResist);
        Assert.Equal(-30d, stats.ChaosResist);
        Assert.Equal(5d, stats.FireResistOver);
        Assert.Equal(4d, stats.ColdResistOver);
        Assert.Equal(1d, stats.LightningResistOver);
    }

    [Fact]
    public void 引擎自己的溢出字段名_不再被当成我们的协议键()
    {
        // ⚠ 这是踩过的坑：引擎里火/电叫 FireResistOver / LightningResistOver、**冰压根没有**、
        //   混沌才叫 ChaosResistOverCap（各元素不统一）。helper 统一用 Total - Resist 算好再发，
        //   所以这几个**引擎原生名字**不该被当成我们的协议键 —— 认了它们就等于拿别的元素的数冒充。
        var response = PobEngineProtocol.Decode("""
            {"id":1,"ok":true,"result":{"stats":{"dps":1,"ehp":2,"life":3,
             "fireResistOverCap":5,"coldResistOverCap":4,"lightningResistOverCap":1}}}
            """)!;

        var stats = PobCompareService.ReadStats(response);

        Assert.NotNull(stats);
        Assert.Null(stats!.FireResistOver);
        Assert.Null(stats.ColdResistOver);
        Assert.Null(stats.LightningResistOver);
    }

    [Fact]
    public void 字段缺失时读成_null_而不是_0()
    {
        // 老 helper / 引擎取不到 → 键不在 → null。**不许发 0 冒充**（见 tao-engine-server.lua）。
        var response = PobEngineProtocol.Decode(
            """{"id":1,"ok":true,"result":{"stats":{"dps":1,"ehp":2,"life":3}}}""")!;

        var stats = PobCompareService.ReadStats(response);

        Assert.NotNull(stats);
        Assert.Null(stats!.FireResist);
        Assert.Null(stats.ColdResist);
        Assert.Null(stats.LightningResist);
        Assert.Null(stats.ChaosResist);
        Assert.Null(stats.FireResistOver);
        Assert.Null(stats.ColdResistOver);
        Assert.Null(stats.LightningResistOver);
    }
}
