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

            rows.Add(Row(entry, result, HardGateFailed(template, item, slotKey)));

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

        // 主指标算不出来时，按 DPS 排名等于按一列 0 排名（名次只剩并列规则）→ 自动改用 EHP。
        // 与逐条词缀收益（PobAffixGainService）用**同一个判据**，别在两处各写一套。
        var effectiveMetric = PobAffixGainService.EffectiveMetric(rows.Any(x => x.DpsUnavailable), metric);

        // 前提：这一批数字是在哪个模板 / 场景 / 主指标 / 引擎代际下算出来的。
        // 界面拿它判「前提已变」（见 BasketComparability）：换了模板 / 场景之后这些数字不再对应当前前提，
        // 数字照留但**不给名次**。主指标优先从基线缓存取（那是这批数字真正用的口径），
        // 缓存拿不到（引擎中途重启过）就退回行里带回的那份 —— 不许猜一个值出来。
        // ⚠ 一个名次都没有时**不去碰 compare**：这一批压根没算过，没有前提可记
        //   （也让「箱子里的旧条目没原文」这条路径完全不依赖引擎）。
        var rankedRows = rows.Where(x => x.IsRanked).ToList();
        if (rankedRows.Count > 0)
        {
            var premise = new BasketPremise(
                template?.Id,
                compare.Context,
                compare.BaselineMetricKey(template) ?? rankedRows[0].PrimaryMetricKey,
                compare.EngineGeneration);

            foreach (var row in rankedRows)
            {
                row.Premise = premise;
            }
        }

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
