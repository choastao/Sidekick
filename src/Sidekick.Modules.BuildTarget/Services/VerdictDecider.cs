namespace Sidekick.Modules.BuildTarget.Services;

using System.Globalization;
using Microsoft.Extensions.Localization;
using Sidekick.Modules.BuildTarget.Localization;
using Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 「这件装备该不该换」的判定。抽成纯函数是为了能单独测试——
/// 判定逻辑曾经错得很难发现（见下），而构造 BuildTargetEvaluator 需要 ItemParser 和整套游戏数据。
///
/// 修过的错：早期把「从 BD 导入的门槛」也当成硬性要求，且把「这件装备没有该条属性」当成「不达标」。
/// 结果用户把自己正穿着的头盔标为当前装备后，照样被判「不建议」——因为那件头盔不是 ES 底子，
/// 在「能量护盾 ≥58」这条上被判不合格。现在：
///   ① 硬性门槛只可能是用户自己勾的；
///   ② 「没这条属性」= 不涉及，不参与判定；
///   ③ 主轴是「相对当前装备的增减」。
/// </summary>
public static class VerdictDecider
{
    /// <summary>
    /// 判定顺序即优先级：
    /// ① 用户自己设的硬性门槛没过 → 不建议（这是他自己的要求）；
    /// ② 这件装备就是该部位当前装备 → 直说，不给换装建议；
    /// ③ 其余以「相对当前装备的增减」为主轴。
    /// </summary>
    public static (Verdict Verdict, string Headline) Decide(
        SlotEvaluation evaluation,
        IStringLocalizer<BuildTargetResources> resources)
    {
        if (evaluation.Checks.Count == 0)
        {
            return (Verdict.Unknown, resources["Result_No_Slot_Target"]);
        }

        // ① 硬性门槛。从 BD 导入的门槛一律是参考值（Required=false），
        //    所以走到这里的 required 一定是用户自己勾的 —— 未达标判「不建议」是合理的。
        var failed = evaluation.Checks.Where(x => x.Required && !x.Pass).ToList();
        if (failed.Count > 0)
        {
            return (Verdict.Bad, string.Format(
                CultureInfo.CurrentCulture,
                resources["Result_Failed"],
                string.Join("、", failed.Select(x => x.Label))));
        }

        // ② 就是身上那件。
        if (evaluation.IsCurrentItem)
        {
            return (Verdict.Good, resources["Result_Is_Current"]);
        }

        // ③ 相对增减。某条目标词缀在新装备上不存在 → New 为 null → Delta 也是 null，
        //    那是「这件装备不涉及这条」，不是「不达标」。
        var deltas = evaluation.Checks.Where(x => x.Delta.HasValue).Select(x => x.Delta!.Value).ToList();

        if (deltas.Count == 0)
        {
            if (evaluation.Checks.All(x => !x.Provided))
            {
                return (Verdict.Warn, resources["Result_None_Provided"]);
            }

            return evaluation.HasEquipped
                ? (Verdict.Warn, resources["Result_Pass_No_Comparable"])
                : (Verdict.Warn, resources["Result_Pass_No_Baseline"]);
        }

        var up = deltas.Count(x => x > 0);
        var down = deltas.Count(x => x < 0);

        if (down == 0 && up > 0)
        {
            return (Verdict.Good, string.Format(CultureInfo.CurrentCulture, resources["Result_Better"], up));
        }

        if (up > 0 && down > 0)
        {
            return (Verdict.Warn, string.Format(CultureInfo.CurrentCulture, resources["Result_Mixed"], up, down));
        }

        if (down > 0)
        {
            return (Verdict.Bad, string.Format(CultureInfo.CurrentCulture, resources["Result_Worse"], down));
        }

        return (Verdict.Warn, resources["Result_Even"]);
    }
}
