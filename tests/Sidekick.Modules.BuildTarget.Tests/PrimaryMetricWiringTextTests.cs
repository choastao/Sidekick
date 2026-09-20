using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Sidekick.Modules.BuildTarget.Localization;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// B1 / S1 这一批新增的界面文案在两个语言里都要有、占位符要一致。
///
/// 为什么单独钉一条：这些键是「标签与数字对不上」「老 helper 静默降级」两条缺陷**唯一**的
/// 可见面 —— 文案取不到时，<c>Resources[key]</c> 会把键名原样显示出来（或者干脆不显示），
/// 而那条保护就悄悄没了。
/// ⚠ 资源是 python 脚本生成的（<c>gen-buildtarget-resx.py</c>），本批手工加了这四个键，
///   同步脚本时别把它们冲掉（见 report-v137-primary-metric-wiring.md）。
/// </summary>
public class PrimaryMetricWiringTextTests
{
    private static readonly ResourceManager Manager = new(
        "Sidekick.Modules.BuildTarget.Localization.BuildTargetResources",
        typeof(BuildTargetResources).Assembly);

    /// <summary>B1：这一列是**所选主指标**时，表头/数值旁边要能看到是哪个指标。</summary>
    private const string PrimaryMetric = "Pob_Primary_Metric";

    public static TheoryData<string> Keys =>
    [
        "Pob_Primary_Metric",          // 既有键：回退档下方那行「本次用的伤害指标：综合 DPS」
        "Pob_Context_Unconfirmed",     // S1 新增：引擎没确认场景
        "Pob_Resist_Guard_Off",        // S1 新增：抗性七项没报全 → 守门未生效
        "Engine_Axis_Label",           // A6 新增：引擎指标那条轴的限定词
        "Threshold_Axis_Label",        // A6 新增：门槛那条轴的限定词
    ];

    [Theory]
    [MemberData(nameof(Keys))]
    public void 文案中英两份都有且占位符一致(string key)
    {
        var english = Manager.GetString(key, CultureInfo.InvariantCulture);
        var chinese = Manager.GetString(key, CultureInfo.GetCultureInfo("zh"));

        Assert.False(string.IsNullOrWhiteSpace(english), $"{key} 缺英文文案");
        Assert.False(string.IsNullOrWhiteSpace(chinese), $"{key} 缺中文文案");
        Assert.Equal(Placeholders(english!), Placeholders(chinese!));

        // 文案里不许再出现旧名字（项目口径）
        Assert.DoesNotContain("Sidekick", english!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sidekick", chinese!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 两条轴的限定词互不混淆()
    {
        // A6：用户要能分清「按引擎指标判」与「按你的门槛判」这两条轴
        var engine = Manager.GetString("Engine_Axis_Label", CultureInfo.GetCultureInfo("zh"))!;
        var threshold = Manager.GetString("Threshold_Axis_Label", CultureInfo.GetCultureInfo("zh"))!;

        Assert.Contains("引擎", engine, StringComparison.Ordinal);
        Assert.Contains("门槛", threshold, StringComparison.Ordinal);
        Assert.NotEqual(engine, threshold);
    }

    [Fact]
    public void 回退档的指标名那行有占位符()
    {
        // {0} 是主指标名（PobPrimaryMetric.DisplayName）；少了它界面就只说「本次用的伤害指标：」
        Assert.Equal(1, Placeholders(Manager.GetString(PrimaryMetric, CultureInfo.InvariantCulture)!));
    }

    /// <summary>
    /// 场景未确认那条文案**只许说「可能」**（审计 R4）。
    ///
    /// 病根：helper 没回 <c>context</c> 只说明「我们没拿到确认」，不等于「引擎一定没覆盖场景」——
    /// 老文案写成「这些数是按 BD 自己的配置算的」是把一个我们不知道的事实说死了。
    /// 本项目口径是「宁可说不确认，也不许说一个我们不知道的事实」。
    /// </summary>
    [Fact]
    public void 场景未确认的文案不许把可能说成事实()
    {
        var chinese = Manager.GetString("Pob_Context_Unconfirmed", CultureInfo.GetCultureInfo("zh"))!;
        var english = Manager.GetString("Pob_Context_Unconfirmed", CultureInfo.InvariantCulture)!;

        Assert.Contains("可能", chinese, StringComparison.Ordinal);
        Assert.DoesNotContain("这些数是按", chinese, StringComparison.Ordinal);   // 旧文案的断言式说法

        Assert.Contains("may not", english, StringComparison.Ordinal);
        Assert.DoesNotContain("the numbers are the build's own config", english, StringComparison.Ordinal);

        // 前半句仍要说清「没拿到什么」：只说「可能不是」而不说清依据，等于把结论悬空
        Assert.Contains("引擎没回报", chinese, StringComparison.Ordinal);
    }

    /// <summary>文案里用到的参数个数（最大占位符下标 + 1）。</summary>
    private static int Placeholders(string format)
    {
        var matches = Regex.Matches(format, @"\{(\d+)");
        return matches.Count == 0 ? 0 : matches.Max(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)) + 1;
    }
}
