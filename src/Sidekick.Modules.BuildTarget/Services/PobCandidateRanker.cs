using Microsoft.Extensions.Logging;
using Sidekick.Game.Parser;
using Sidekick.Game.Parser.Items;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// C2a：把备选篮里的一批候选**逐个试穿**，按 DPS / EHP 的增减排序。
///
/// 复用 <see cref="PobCompareService"/>（它已经把「同一模板 + 同一引擎代次只 load_build 一次」
/// 和串行闸都做好了），所以这里的成本是 **1 次 load_build + N 次 equip**：
/// 热引擎单次 equip 约 15 ms，十几件候选在几百毫秒量级。
///
/// 三条纪律：
///   1. **只算得出来的才排名**：算不出来的行照样返回（带原因），由界面如实显示；
///   2. **引擎级失败立刻停**（没装引擎 / 超时崩溃）：后面的候选不用再白试一遍，
///      但**已算出来的保持排名** —— 一次超时不该让整批结果消失；
///   3. **取消**：`cancellationToken` 一路透传到 `PobCompareService`（它已保证「调用方取消不杀引擎」），
///      但**当前 UI 调用点没有传 token**（界面没有「取消排序」按钮），所以「可取消」目前是能力而非行为。
///      ⚠ 将来接线时**必须同时 catch `OperationCanceledException`** —— `MeasureTextAsync` 对取消是
///      **rethrow** 的，直接从 `@onclick` 处理器里抛出去会变成未处理异常（审计 C2 第二轮 G）。
/// </summary>
public class PobCandidateRanker(
    PobCompareService compare,
    ItemParser itemParser,
    BuildTargetEvaluator evaluator,
    ILogger<PobCandidateRanker> logger)
{
    public async Task<CandidateRanking> RankAsync(
        BuildTargetTemplate? template,
        IReadOnlyList<CandidateBasketItem> candidates,
        CandidateRankMetric metric = CandidateRankMetric.Dps,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<CandidateRankRow>();

        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = TryParse(entry.Text);
            if (item == null)
            {
                // v3.7 之前加进篮子的条目没有存物品原文（那时只需要算好的贡献值）→ 如实说，不猜
                rows.Add(Row(entry, PobCompareStatus.MissingText));
                continue;
            }

            // 部位以**这件物品自己**解析出来的为准，篮子里的 SlotKey 只当兜底
            // （用户可能把同一件东西在不同上下文里加过两次）
            var slotKey = SlotKeys.ResolveFor(item).FirstOrDefault() ?? entry.SlotKey;
            var result = await compare.CompareAsync(template, item, slotKey, cancellationToken);

            var row = Row(entry, result, HardGateFailed(template, item, slotKey));

            // 前提**逐行盖**（不是整批一个）：一批算到一半用户切了场景 / 引擎被杀重启 /
            // 基线换了个回退档，后面的行与前面的行就不是同一个前提算出来的。
            // 主指标取**基线缓存里那份**（这批数字真正用的口径）；缓存拿不到（引擎中途重启过）
            // 就退回这一行自己带回的那份 —— 不许猜一个值出来。
            // ⚠ 必须在**这一行的 await 之后**读：基线是在那次调用里载入的，之前读只会拿到 null。
            if (row.IsRanked)
            {
                row.Premise = new BasketPremise(
                    template?.Id,
                    compare.Context,
                    compare.BaselineMetricKey(template) ?? row.PrimaryMetricKey,
                    compare.EngineGeneration);
            }

            rows.Add(row);

            if (result.Status == PobCompareStatus.EngineUnavailable)
            {
                logger.LogWarning(
                    "[BuildTarget] Candidate ranking stopped early: the engine is unavailable ({Error})",
                    result.Error);

                // 后面的候选在同一个引擎上必然同样失败 —— 不再逐个白试，但把剩下的如实列出来
                foreach (var remaining in Skip(candidates, entry))
                {
                    rows.Add(Row(remaining, PobCompareStatus.EngineUnavailable));
                }

                break;
            }
        }

        // ⚠ 判据是 **DpsUnavailable**（= 三个伤害字段全算不出来），与逐条词缀收益（PobAffixGainService）
        //   用**同一个判据**，别在两处各写一套。
        //
        //   回退档（基线 TotalDPS = 0、CombinedDPS 有数）里每一行的 MetricValue 都是那个指标的数，
        //   照 DPS 排是实话 —— 只有「伤害一个数都没有」的行才该让整批改按 EHP 排。
        //
        //   ⚠ **判据只看「算出来的行」（IsRanked）**：没算出来的行（`MissingText`：v3.7 之前的条目没存物品原文、
        //     基底认不出、转换失败）走的是另一个工厂，它们的 `DpsUnavailable` 只是 `PobCompareResult.Not(...)`
        //     的**默认值 false** —— 把它算进判据，会在「批次里混了一行没算出来的」时把降级判反，
        //     于是重新出现「表头写按 DPS 排、整列 DPS 是「—」、名次实际由 EHP 决定」那种自相矛盾。
        var effectiveMetric = PobAffixGainService.EffectiveMetric(
            ShouldFallbackToEhp(rows),
            metric);

        logger.LogInformation(
            "[BuildTarget] Candidate ranking done: {Ranked}/{Total} ranked by {Metric}{Fallback}",
            rows.Count(x => x.IsRanked),
            rows.Count,
            effectiveMetric,
            effectiveMetric == metric ? "" : "（DPS 算不出来，自动改用 EHP）");

        return CandidateRanking.Sort(rows, effectiveMetric);
    }

    private static IEnumerable<CandidateBasketItem> Skip(IReadOnlyList<CandidateBasketItem> candidates, CandidateBasketItem current)
    {
        var seen = false;
        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate, current))
            {
                seen = true;
                continue;
            }

            if (seen)
            {
                yield return candidate;
            }
        }
    }

    private Item? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return itemParser.ParseItem(text);
        }
        catch (Exception ex)
        {
            // 文本坏了不该让整批排序挂掉：这一行标出来，其余照算
            logger.LogWarning(ex, "[BuildTarget] Candidate text could not be parsed, skipping it");
            return null;
        }
    }

    /// <summary>
    /// 整批是否该改按 EHP 排：**只看算出来的行**里有没有「伤害一个数都没有」的。
    /// 没算出来的行不参与 —— 它们的 <see cref="CandidateRankRow.DpsUnavailable"/> 是默认值 false，
    /// 拿默认值当事实正是本项目最容易出错的地方（审计 S2）。
    /// </summary>
    internal static bool ShouldFallbackToEhp(IEnumerable<CandidateRankRow> rows) =>
        rows.Any(x => x.IsRanked && x.DpsUnavailable);

    private static CandidateRankRow Row(CandidateBasketItem entry, PobCompareResult result, bool hardGateFailed)
    {
        // 引擎指标那条轴的判定与「相对当前装备」的帕累托关系都在这里算好、随行带着走 ——
        // 面板只负责显示，不再自己判一遍（免得两处各写一套判据）。
        var input = ItemVerdictInput.From(result, hardGateFailed);
        var (verdict, reasonKey) = ItemVerdictDecider.Decide(input);

        return new()
        {
            Id = entry.Id,
            Name = entry.Name,
            BaseType = entry.BaseType,
            SlotKey = entry.SlotKey,
            Status = result.Status,
            DpsDelta = result.DpsDelta,
            // 排序用的数值：**所选主指标的增减**（候选侧 − 基线侧，同一个 key）。
            // 回退到 Combined / Full 时它才是那列的真值 —— 用 TotalDPS 算会得到一列 0，
            // 而表头／图例写着「按综合 DPS 排」（见审计 B1）。排序要的是**差值**不是绝对值。
            MetricValue = result.MetricDelta,
            EhpDelta = result.EhpDelta,
            DpsPercent = result.DpsPercent,
            EhpPercent = result.EhpPercent,
            UnmappedAffixes = result.UnmappedAffixes,
            EngineUnsupportedLines = result.EngineUnsupportedLines,
            PrimaryMetricKey = result.PrimaryMetricKey,
            DpsUnavailable = result.DpsUnavailable,
            Error = result.Error,
            Verdict = verdict,
            VerdictReasonKey = reasonKey,
            Pareto = Pareto.Compare(input.DpsPercent, input.EhpPercent),
        };
    }

    /// <summary>
    /// 这件候选有没有踩到用户自己勾的硬性门槛（引擎指标判定的第 ① 条）。
    ///
    /// 复用**非引擎那条轴**的评估器（<see cref="BuildTargetEvaluator"/>）拿数据 ——
    /// **不重写一套门槛匹配**（那正是「两处各写一套判据」的病根）。
    /// 评估失败（物品文本坏了之类）就当没踩到：宁可少一个理由，也不编一个结论。
    /// </summary>
    private bool HardGateFailed(BuildTargetTemplate? template, Item item, string slotKey)
    {
        if (template == null)
        {
            return false;
        }

        try
        {
            var evaluation = evaluator.Evaluate(template, item);
            var slot = evaluation.Slots.FirstOrDefault(x => x.SlotKey == slotKey)
                       ?? evaluation.Slots.FirstOrDefault();

            return slot?.HardGateFailed == true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[BuildTarget] Candidate hard-gate evaluation failed; treating the gate as passed");
            return false;
        }
    }

    private static CandidateRankRow Row(CandidateBasketItem entry, PobCompareStatus status) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        BaseType = entry.BaseType,
        SlotKey = entry.SlotKey,
        Status = status,
    };
}
