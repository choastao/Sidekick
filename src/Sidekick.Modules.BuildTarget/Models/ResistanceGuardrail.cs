namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 「已经顶到上限的抗性被拉低」的守门（纯函数，单测直接调）。
///
/// 为什么要有它：抗性顶到上限之后，再堆抗性收益是 0，但**掉下来**是实打实的缺口。
/// 引擎给的 DPS / EHP 涨了，很可能就是拿「本来溢出的元素抗性」换来的 ——
/// 只看 DPS / EHP 会把它判成「提升」，用户换上去才发现元素抗性掉出上限。
///
/// 数据全部来自引擎（`mainOutput` 的有效抗性与溢出，见 <see cref="PobStats"/>），
/// **不在我们这边另建一套抗性账本**。
/// </summary>
public static class ResistanceGuardrail
{
    /// <summary>
    /// 「已经顶到上限的抗性被拉低」= 缺口变糟。
    ///
    /// 条件（火 / 冰 / 电任一满足即算）：
    /// 当前该项**有溢出**（<c>OverCap &gt; 0</c>，即已在上限之上）**且**候选的有效抗性 &lt; 当前的有效抗性。
    ///
    /// 数据缺失（任一侧为 null）= **不知道就不猜** → 一律 false。
    /// 具体地：<paramref name="current"/> / <paramref name="candidate"/> 为 null，
    /// 或该项的 OverCap / 有效抗性任一为 null，都当「不知道」处理 ——
    /// ⚠ **不许把缺失当 0**：老 helper 不报这几个字段时，「溢出 = 0」会让这条守门整个失效，
    /// 而看起来又像是「检查过了，没问题」。
    /// </summary>
    /// <summary>默认元素抗上限（游戏默认 75%）；BD 若有 +上限 词缀，靠「有溢出」那条分支兜住。</summary>
    public const double DefaultElementalCap = 75.0;

    /// <summary>容差（百分点）：低于这个幅度的波动不算「拉低」。</summary>
    public const double Tolerance = 0.5;

    public static bool Worsened(PobStats? current, PobStats? candidate)
    {
        if (current == null || candidate == null)
        {
            return false;
        }

        return Worsened(current.FireResistOver, current.FireResist, candidate.FireResist)
               || Worsened(current.ColdResistOver, current.ColdResist, candidate.ColdResist)
               || Worsened(current.LightningResistOver, current.LightningResist, candidate.LightningResist);
    }

    /// <summary>
    /// 单项：当前这项**已经顶到上限**且候选把它拉低了。
    ///
    /// 「顶到上限」两条判据取或（任一条成立就算）：
    ///   ① 当前有溢出（<c>over &gt; 0</c>）—— 上限被抬到多少都成立，**最可靠**；
    ///   ② 当前有效抗性 ≥ <see cref="DefaultElementalCap"/> − <see cref="Tolerance"/>（默认元素抗上限 75）。
    ///      ⚠ 已知局限：BD 把某元素上限抬到 75 以上、且当前正好卡在那个上限（溢出 = 0）时会漏判。
    ///      宁可漏判，也不许把「不知道」当「到上限」——那会把正常取舍说成缺口变糟。
    ///
    /// 「拉低」= 候选有效抗性比当前低超过 <see cref="Tolerance"/>（引擎重算有浮点噪声，0.1 的差不当信号）。
    /// 数据缺失（任一侧 null）= **不知道就不猜** → false。
    /// </summary>
    private static bool Worsened(double? currentOver, double? currentResist, double? candidateResist)
    {
        if (currentResist is not { } currentValue || candidateResist is not { } candidateValue)
        {
            return false;
        }

        var atCap = (currentOver is { } over && over > 0)
                    || currentValue >= DefaultElementalCap - Tolerance;

        return atCap && candidateValue < currentValue - Tolerance;
    }
}
