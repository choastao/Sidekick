namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 「这一行是在什么前提下算出来的」。
///
/// 备选篮里的每个数字都是**在某个模板 + 某个场景下试穿算出来的**；
/// 用户换了模板 / 场景之后，旧数字还挂在界面上就变成了另一回事 ——
/// 把两个前提下的行混在一起排名，等于拿不同口径的数比大小（静默错数，比报错坏得多）。
///
/// 四个字段就是**全部**会影响这个数值的前提：
/// 模板（换模板 = 换了整套 BD）、场景（BUILD / MAP / BOSS，见 <see cref="PobContexts"/>）、
/// 主指标（TotalDPS / CombinedDPS / FullDPS，见 <see cref="PobPrimaryMetric"/>）、
/// 引擎代际（引擎重启过 = 里面那份 BD 没了，数值不是同一代算的）。
/// </summary>
public sealed record BasketPremise(string? TemplateId, string? Context, string? PrimaryMetricKey, int EngineGeneration);

/// <summary>
/// 备选篮的「可比性」判定（纯逻辑，单测直接调）。
///
/// 两条用法：
///   · <see cref="AreComparable"/>：**所有已算出的行**必须共享同一个前提，才允许给「最优 / 名次」；
///     前提不一致时数字照留，但名次不能给（少了这一条就会把两套前提的行排成一条榜）。
///   · <see cref="IsStale"/>：某一行与**当前**前提不一致 = 陈旧，界面上要打标记
///     并把它从「名次」里摘出去（数字保留，因为用户可能正想对照着看）。
/// </summary>
public static class BasketComparability
{
    /// <summary>
    /// 所有**已算出的**行必须共享同一个前提（模板 + 场景 + 主指标 + 引擎代际）才给「最优」。
    ///
    /// 空集合 / 只有一行 → true（没有「不一致」可言）。
    /// </summary>
    public static bool AreComparable(IEnumerable<BasketPremise> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        BasketPremise? first = null;
        foreach (var row in rows)
        {
            if (row == null)
            {
                continue;
            }

            if (first == null)
            {
                first = row;
                continue;
            }

            if (!Same(first, row))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>与当前前提不一致的行 = 陈旧。</summary>
    public static bool IsStale(BasketPremise row, BasketPremise current)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(current);

        return !Same(row, current);
    }

    /// <summary>
    /// 前提是否相同。
    /// 场景用大小写不敏感（协议里都是大写，别因为大小写多判一次陈旧）；
    /// 模板 id / 主指标键是**字面量标识**，必须逐字相同。
    /// </summary>
    private static bool Same(BasketPremise a, BasketPremise b) =>
        string.Equals(a.TemplateId, b.TemplateId, StringComparison.Ordinal)
        && string.Equals(a.Context, b.Context, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.PrimaryMetricKey, b.PrimaryMetricKey, StringComparison.Ordinal)
        && a.EngineGeneration == b.EngineGeneration;
}
