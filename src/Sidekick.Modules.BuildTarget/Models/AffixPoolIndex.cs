using Sidekick.Game.TradeStats;

namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 「交易站词缀（当前语言）→ 权重表词缀池」的翻译层。
///
/// 为什么不能直接比文本：权重表（affix-weights.json）的词缀文本永远是英文
/// （"+(6-10)% to Fire Resistance"），而词缀库跟随物品语言 —— 中文客户端拿到的是
/// 「#%火焰抗性」，两边硬比一条都对不上，按部位过滤会把整个库清空。
///
/// 词缀库的 id 是跨语言同一套（explicit.stat_3372524247 在英文词缀库里就是
/// "#% to Fire Resistance"），所以这里拿一份英文词缀库当字典，把当前语言的词缀用 id
/// 翻成英文原文，再用 <see cref="ModWeight.Normalize"/>（和
/// <see cref="Services.ExpectedCostCalculator"/> 同一套归一化规则）在权重表里找条目。
/// 英文词缀库是随包发布的数据（_data\&lt;game&gt;\en\trade-stats.json），不额外造数据。
///
/// 匹配方向也和期望成本那边一致：词缀库文本 ⊆ 权重表词缀文本。
/// 只会多认、不会少认，所以最坏情况是「过滤得不狠」，不会把能出的词缀藏起来。
/// </summary>
public sealed class AffixPoolIndex
{
    private readonly IReadOnlyList<ModWeight> mods;
    private readonly Dictionary<string, List<string>> englishTextsByStatId = [];
    private readonly Dictionary<string, IReadOnlyList<ModWeight>> modsByStatId = [];
    private readonly object gate = new();

    /// <param name="mods">权重表里的全部词缀。</param>
    /// <param name="englishStats">英文交易站词缀库（用来把 id 翻成权重表能懂的英文文本）。</param>
    public AffixPoolIndex(IReadOnlyList<ModWeight> mods, IEnumerable<TradeStatDefinition> englishStats)
    {
        this.mods = mods ?? [];

        if (englishStats is null)
        {
            return;
        }

        foreach (var stat in englishStats)
        {
            if (stat is null || string.IsNullOrWhiteSpace(stat.Id))
            {
                continue;
            }

            var normalized = ModWeight.Normalize(stat.Text);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (!englishTextsByStatId.TryGetValue(stat.Id, out var texts))
            {
                englishTextsByStatId[stat.Id] = texts = [];
            }

            if (!texts.Contains(normalized))
            {
                texts.Add(normalized);
            }
        }
    }

    /// <summary>英文词缀库里的统计 id 数量（0 说明英文词缀库读不到）。</summary>
    public int StatIdCount => englishTextsByStatId.Count;

    /// <summary>
    /// 这条词缀在权重表里对应的条目。
    /// 先按 id 查英文原文；id 查不到（英文库里没有这条）时退回用传进来的原文 ——
    /// 英文客户端、或英文库缺这条时都能兜住，不猜。
    /// </summary>
    private IReadOnlyList<ModWeight> ModsFor(string? statId, string? text)
    {
        var key = !string.IsNullOrWhiteSpace(statId) ? statId : ModWeight.Normalize(text);

        lock (gate)
        {
            if (modsByStatId.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var patterns = new List<string>();
            if (!string.IsNullOrWhiteSpace(statId) && englishTextsByStatId.TryGetValue(statId, out var english))
            {
                patterns.AddRange(english);
            }

            if (patterns.Count == 0)
            {
                var own = ModWeight.Normalize(text);
                if (own.Length > 0)
                {
                    patterns.Add(own);
                }
            }

            var matched = new List<ModWeight>();
            foreach (var mod in mods)
            {
                var modText = mod.NormalizedText;
                if (modText.Length == 0)
                {
                    continue;
                }

                foreach (var pattern in patterns)
                {
                    if (modText.Contains(pattern, StringComparison.Ordinal))
                    {
                        matched.Add(mod);
                        break;
                    }
                }
            }

            modsByStatId[key] = matched;
            return matched;
        }
    }

    /// <summary>
    /// 这条词缀在权重表里有没有对应条目。
    /// false = 权重表里根本没有这条（不属于列举基底），按标签集过滤时会被一并过滤掉。
    /// </summary>
    public bool IsListed(string? statId, string? text) => ModsFor(statId, text).Count > 0;

    /// <summary>
    /// 这条词缀能不能出现在这个标签集里：任一对应条目的 <see cref="ModWeight.SpawnsOn"/>
    /// 成立就算能出（并集，和词缀池的判定完全一致）。
    /// 标签集为空 = 判不了，返回 false，调用方自己决定降级成「不过滤」。
    /// </summary>
    public bool SpawnsOn(string? statId, string? text, IReadOnlyCollection<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return false;
        }

        foreach (var mod in ModsFor(statId, text))
        {
            if (mod.SpawnsOn(tags))
            {
                return true;
            }
        }

        return false;
    }
}
