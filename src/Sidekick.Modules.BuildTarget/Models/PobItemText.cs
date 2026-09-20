using System.Globalization;
using System.Text;
using Sidekick.Game.Parser.Items;
using Sidekick.Game.Parser.Stats;

namespace Sidekick.Modules.BuildTarget.Models;

/// <summary>
/// 把 Sidekick 解析出来的物品转成 **PoB2 认得的英文 raw 文本**（纯函数，便于单测）。
///
/// 为什么需要这一步：剪贴板里的物品是**繁体中文**，而 PoB 引擎的词缀解析器只认英文。
/// 两条转换路径都走 Sidekick 已经准备好的英文数据（它为了「查价走英文站」本来就加载了）：
///   · 名称/基底 → <see cref="Item.InvariantTradeItem"/> / <see cref="Item.InvariantDefinition"/>
///   · 词缀      → stat 的 <c>TradeIds</c> 去英文 trade-stats 表里查模板（<c>#</c> 是数值占位符），
///                 再用解析出来的数值按顺序填回去。
///
/// ⚠ **不认识的词缀绝不静默丢弃**：<see cref="BuildResult.Skipped"/> 会如实报出条数，
/// 调用方要么提示用户「有 N 条词缀引擎不认识、结论偏乐观」，要么干脆不显示结论。
/// 同样地，基底名取不到英文时不猜 —— <see cref="BuildResult.BaseIdentified"/> 为 false，
/// 调用方按「这件物品算不了」处理（否则 PoB 会把它当成空物品，试穿结果 = 没有变化，属于假结论）。
/// </summary>
public static class PobItemText
{
    /// <summary>伪属性与未识别的文本行不是装备上的真词缀，给 PoB 只会添乱。</summary>
    private static readonly StatCategory[] NonAffixCategories = [StatCategory.Pseudo, StatCategory.Undefined];

    public sealed record BuildResult(string Text, int TotalStats, int MappedStats, bool BaseIdentified, int AnnotationStripped = 0)
    {
        public int Skipped => TotalStats - MappedStats;
    }

    public static BuildResult Build(Item item, IReadOnlyDictionary<string, string> invariantStatText)
    {
        var lines = new List<string> { "Rarity: " + RarityText(item.Properties.Rarity) };

        // ---- 名称 + 基底（PoB 靠这两行认物品）----
        var type = FirstNonEmpty(
            item.InvariantTradeItem?.Type,
            item.InvariantDefinition?.Name);
        var baseIdentified = !string.IsNullOrWhiteSpace(type);
        if (!baseIdentified)
        {
            // 兜底用当前语言的基底名：PoB 认不出，但至少让文本看起来完整（调用方不会再拿它去试穿）
            type = item.Type;
        }

        var name = FirstNonEmpty(item.InvariantTradeItem?.Name, item.InvariantDefinition?.Name);
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, type, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add(name!);
        }
        else if (NeedsTitleLine(item.Properties.Rarity) && !string.IsNullOrWhiteSpace(type))
        {
            // ⚠ 稀有/传奇物品在 PoB 眼里是「名字 + 基底」两行：它把 Rarity 之后的第一行当 title，
            //   **第二行才是 baseName**。英文名与基底同名时（名称取不到、或本来就相同）上面那条守卫
            //   会不发名字行，于是唯一那行被当成 title → baseName = nil →
            //   PoB 内部 BuildRaw 直接炸：`Classes/Item.lua:1867: attempt to concatenate field 'baseName'`。
            //   实测（tao-charm-test.py，试穿到 Charm 1）：
            //     RARE + 单行基底 → 报错；RARE + 同名两行 → 正常且词缀生效；MAGIC + 单行 → 正常。
            //   所以这里把基底重复一次当 title 行，保住稀有度语义（换成 MAGIC/NORMAL 会改词缀条数规则）。
            lines.Add(type!);
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            lines.Add(type!);
        }

        // ---- 防御/等级等属性行（PoB 用 "Keyword: value" 识别）----
        AddIfPositive(lines, "Energy Shield", item.Properties.EnergyShield);
        AddIfPositive(lines, "Armour", item.Properties.Armour);
        AddIfPositive(lines, "Evasion Rating", item.Properties.EvasionRating);
        AddIfPositive(lines, "Ward", item.Properties.RunicWard);
        AddIfPositive(lines, "Quality", item.Properties.Quality);
        AddIfPositive(lines, "Item Level", item.Properties.ItemLevel);

        // ---- 词缀：隐式在前（PoB 用 "Implicits: N" 声明条数），显式在后 ----
        var totalStats = 0;
        var mapped = 0;
        var annotationStripped = 0;
        var implicitLines = new List<string>();
        var explicitLines = new List<string>();

        foreach (var stat in item.Stats)
        {
            if (NonAffixCategories.Contains(stat.Category))
            {
                continue;
            }

            totalStats++;

            var found = FindTemplate(stat, invariantStatText);
            if (found is not { } match)
            {
                continue;
            }

            var line = FillValues(match.Template, stat.Values);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // ⚠ 模板里的 # 是数值占位符，**必须全部填上**。填不满说明这条词缀的数值结构与
            //   英文模板对不上；带 # 的行喂给 PoB 会在 Modules/ItemTools.lua 的 formatValue 里
            //   对 nil 做算术，抛错的是**整个物品**——一条坏词缀就能让整次试穿失败
            //   （本次实测：+60 最大生命那条模板 # 多于解析出的数值，整件头盔算不出来）。
            //   所以当「引擎不认识这条」丢掉，Skipped 计数会如实反映出来。
            if (line.Contains('#'))
            {
                continue;
            }

            line = ApplySign(line, match.RequiresPlus);

            mapped++;
            if (match.AnnotationStripped)
            {
                annotationStripped++;
            }

            if (stat.Category == StatCategory.Implicit)
            {
                implicitLines.Add(line);
            }
            else
            {
                explicitLines.Add(line);
            }
        }

        if (implicitLines.Count > 0)
        {
            lines.Add("Implicits: " + implicitLines.Count.ToString(CultureInfo.InvariantCulture));
        }

        lines.AddRange(implicitLines);
        lines.AddRange(explicitLines);

        return new BuildResult(string.Join('\n', lines) + "\n", totalStats, mapped, baseIdentified, annotationStripped);
    }

    /// <summary>
    /// 一条 stat 可能匹配到多个定义，取第一个能在英文表里查到的模板。
    /// 查不到就是「引擎不认识这条词缀」—— 交给调用方如实报出来。
    ///
    /// 顺带把**命中的那条定义的平添符号**带出来：英文 trade-stats 的模板里没有行首 `+`
    /// （它只是文本模板），而游戏/PoB 的定义文本是 `+# to maximum Life` —— 见 <see cref="ApplySign"/>。
    ///
    /// **带尾部注解的模板排在后面**（见 <see cref="StripAnnotation"/>）：同一个 stat 的多个 tradeId
    /// 里通常同时存在 `#% increased Attack Speed (Local)` 和干净的 `#% increased Attack Speed`，
    /// 而前者会让 PoB 整行忽略 —— 优先取干净的那条才是正确形状（也就是游戏英文客户端复制出来
    /// 的样子）。只有在**全都带注解**时才退回「剥掉注解」（剥掉仍比被整行忽略好）。
    /// </summary>
    private static Match? FindTemplate(
        Stat stat,
        IReadOnlyDictionary<string, string> invariantStatText)
    {
        Match? annotatedFallback = null;

        foreach (var definition in stat.Definitions)
        {
            if (definition.TradeIds == null)
            {
                continue;
            }

            foreach (var id in definition.TradeIds)
            {
                if (!invariantStatText.TryGetValue(id, out var template) || string.IsNullOrWhiteSpace(template))
                {
                    continue;
                }

                var requiresPlus = RequiresPlusSign(definition.Text);
                var stripped = StripAnnotation(template);

                if (stripped == null)
                {
                    return new Match(template, requiresPlus, false);
                }

                annotatedFallback ??= new Match(stripped, requiresPlus, true);
            }
        }

        return annotatedFallback;
    }

    /// <summary>命中模板的形状：模板文本、要不要补 `+`、以及是否剥掉了尾部注解。</summary>
    private readonly record struct Match(string Template, bool RequiresPlus, bool AnnotationStripped);

    /// <summary>
    /// trade-stats 里官方交易站的**展示注解**（`(Local)` 40 条 / `(Jewel)` 4 条 / `(Global)` 2 条）——
    /// **不是给装备文本用的语法**。
    ///
    /// ⚠ 实测（`pob2-engine/tao-annotation2-test.py`，别删这段结论）：带注解的行喂给 PoB 等于**整行消失**
    /// （同一件头盔、同一套 BD，只改追加的那一行）：
    ///   · 对照 `+60 to maximum Life`            → EHP 23554 → 23810（生效，证明探测链没坏）
    ///   · `+60 to maximum Life (Local)`         → 23554（**与基线逐位相同 = 被忽略**）
    ///   · 对照 `+60 to maximum Energy Shield`   → 24292（生效）
    ///   · `+60 to maximum Energy Shield (Local)`→ 23554（被忽略）
    /// 而「攻速 / 护甲% / 闪避% / 格挡% / 命中 / 最大能量护盾」这一族在 zh 数据里的
    /// **第一个可查到模板恰好就是带注解的那条**（护甲% 等 8 族另有干净模板，格挡只有注解版）→
    /// 不处理就是又一次「Skipped = 0 但数字算少一块」的假成功。
    /// </summary>
    private static string? StripAnnotation(string template)
    {
        foreach (var annotation in TrailingAnnotations)
        {
            if (!template.EndsWith(annotation, StringComparison.Ordinal))
            {
                continue;
            }

            var stripped = template[..^annotation.Length].TrimEnd();
            return stripped.Length > 0 ? stripped : null;
        }

        return null;
    }

    /// <summary>官方交易站往展示文本尾部加的注解（见 <see cref="StripAnnotation"/>）。</summary>
    private static readonly string[] TrailingAnnotations = ["(Local)", "(Jewel)", "(Global)"];

    /// <summary>定义文本以 <c>+</c> 开头 = 这条是「平添」型词缀（`+# to maximum Life`）。</summary>
    private static bool RequiresPlusSign(string? definitionText) =>
        definitionText != null && definitionText.StartsWith('+');

    /// <summary>
    /// 给平添型词缀补上行首 `+`。
    ///
    /// 为什么必须有这一步（实测，别删）：PoB2 对**缺 `+` 的行是静默忽略**的 ——
    /// 同一件头盔追加 `60 to maximum Life` 与不追加，DPS/EHP 逐位相同（引擎直接当它不存在）；
    /// 追加 `+60 to maximum Life` 才会生效（EHP 23554 → 23810）。
    /// 而英文 trade-stats.json 的模板就是 `# to maximum Life`（`+` 在定义正则里、是捕获组外的字面量），
    /// 于是「引擎不认识的词缀条数」显示 0、数字却算少了一块 —— 假成功，比报错更坏。
    /// 数据侧对照：en `stats.json` 里 1008 条定义文本以 `+` 开头（zh 287 条，如 `+#最大生命`）。
    /// 模板自带符号的（trade-stats 里有 285 处）不重复补。
    /// </summary>
    private static string ApplySign(string line, bool requiresPlus)
    {
        if (!requiresPlus || line.Length == 0 || line[0] is '+' or '-')
        {
            return line;
        }

        return "+" + line;
    }

    /// <summary>把模板里的 <c>#</c> 按顺序替换成数值。数值不够时保留 #（PoB 会当它是通配，不编数字）。</summary>
    private static string FillValues(string template, IReadOnlyList<double> values)
    {
        if (!template.Contains('#'))
        {
            return template;
        }

        var builder = new StringBuilder(template.Length + 16);
        var index = 0;
        foreach (var ch in template)
        {
            if (ch == '#' && index < values.Count)
            {
                builder.Append(FormatNumber(values[index++]));
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string FormatNumber(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void AddIfPositive(List<string> lines, string keyword, int value)
    {
        if (value > 0)
        {
            lines.Add(keyword + ": " + value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    /// <summary>
    /// 稀有/传奇物品要不要「名字 + 基底」两行 —— 见 <see cref="Build"/> 里那段说明
    /// （只发一行会让 PoB 把基底当 title，baseName 变 nil 直接炸）。
    /// </summary>
    private static bool NeedsTitleLine(Rarity rarity) => rarity is Rarity.Rare or Rarity.Unique;

    /// <summary>PoB 的 Rarity 行要全大写。</summary>
    private static string RarityText(Rarity rarity) => rarity switch
    {
        Rarity.Normal => "NORMAL",
        Rarity.Magic => "MAGIC",
        Rarity.Rare => "RARE",
        Rarity.Unique => "UNIQUE",
        Rarity.Gem => "GEM",
        Rarity.Currency => "CURRENCY",
        _ => "RARE",
    };
}
