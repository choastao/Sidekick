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
///   3. **可取消**：批量会传 token，`PobCompareService` 侧已保证调用方取消不会杀引擎。
/// </summary>
public class PobCandidateRanker(
    PobCompareService compare,
    ItemParser itemParser,
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

            rows.Add(Row(entry, result));

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

        logger.LogInformation(
            "[BuildTarget] Candidate ranking done: {Ranked}/{Total} ranked by {Metric}",
            rows.Count(x => x.IsRanked),
            rows.Count,
            metric);

        return CandidateRanking.Sort(rows, metric);
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

    private static CandidateRankRow Row(CandidateBasketItem entry, PobCompareResult result) => new()
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
        Error = result.Error,
    };

    private static CandidateRankRow Row(CandidateBasketItem entry, PobCompareStatus status) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        BaseType = entry.BaseType,
        SlotKey = entry.SlotKey,
        Status = status,
    };
}
