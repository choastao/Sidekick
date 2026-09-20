using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Localization;
using Sidekick.Modules.BuildTarget.Localization;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>一次 BD 导入的结果。</summary>
public class PobImportResult
{
    public BuildTargetTemplate? Template { get; set; }

    /// <summary>失败原因（用户可读），成功时为 null。</summary>
    public string? Error { get; set; }

    /// <summary>过程提示（跳过的部位、识别到的件数等）。</summary>
    public List<string> Notes { get; set; } = [];
}

/// <summary>
/// 导入 Path of Building 2 的 BD（分享码或 pobb.in 链接），生成一份目标模板。
///
/// 为什么要在导入时就把数值抽出来：
/// PoB 的物品文本是它自己的英文格式，而 Sidekick 的解析器按「客户端语言」（我们是中文）解析，
/// 对不上。所以这里不走解析器，直接把词缀行里的数值抽出来存进 EquippedStats，
/// 评估时查表即可，与客户端语言无关。
///
/// 生成的模板包含：
/// 1. 各部位门槛 = 这套 BD 该部位装备的实际数值（照这套 BD 凑装备）
/// 2. 角色总目标 = 全身装备合计（只含装备、不含天赋，与界面「装备合计」口径一致）
/// 3. 当前装备快照 = 这套 BD 的装备，作为「换新装备是提升还是下降」的基准
/// </summary>
public class PobBuildImporter(PoeNinjaClient poeNinja, IStringLocalizer<BuildTargetResources> resources)
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>PoB 部位名 -&gt; 本模块的部位 key。</summary>
    private static readonly Dictionary<string, string> SlotMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helmet"] = SlotKeys.Helmet,
        ["Body Armour"] = SlotKeys.BodyArmour,
        ["Gloves"] = SlotKeys.Gloves,
        ["Boots"] = SlotKeys.Boots,
        ["Weapon 1"] = SlotKeys.Weapon,
        ["Weapon 2"] = SlotKeys.Offhand,
        ["Amulet"] = SlotKeys.Amulet,
        ["Ring 1"] = SlotKeys.Ring1,
        ["Ring 2"] = SlotKeys.Ring2,
        ["Belt"] = SlotKeys.Belt,
    };

    /// <summary>PoB 物品文本里的元数据行前缀（不是词缀）。</summary>
    private static readonly string[] MetaPrefixes =
    [
        "rarity:", "prefix:", "suffix:", "quality:", "sockets:", "rune:", "levelreq:",
        "implicits:", "catalyst:", "catalystquality:", "variant:", "selected variant:",
        "crafted:", "corrupted", "mirrored", "item class:", "unique id:", "radius:",
        "limited to:", "source:", "note:", "implicit:", "explicit:", "bonded:",
    ];

    /// <summary>底子自带的属性行（不算词缀目标）。</summary>
    private static readonly Regex PropertyRegex = new(
        @"^(armour|evasion|evasion rating|energy shield|physical damage|elemental damage|fire damage|" +
        @"cold damage|lightning damage|chaos damage|critical hit chance|attacks per second|" +
        @"weapon range|chance to block|spirit|mana|life|quality|rune)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnnotationRegex = new(@"\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex RangeRegex = new(@"\((\d+(?:\.\d+)?)-(\d+(?:\.\d+)?)\)", RegexOptions.Compiled);
    private static readonly Regex AddsRegex = new(
        @"^Adds (\d+(?:\.\d+)?) to (\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"([+-]?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex PobbRegex = new(@"pobb\.in/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>角色总目标只收这些"会直接相加"的词缀，避免把 DPS 类噪音也塞进去。</summary>
    private static readonly string[] CharacterKeys =
    [
        "Life", "EnergyShield", "IncreasedEnergyShield", "Mana", "Armour", "Evasion", "Spirit",
        "FireRes", "ColdRes", "LightningRes", "ChaosRes", "AllEleRes",
        "Strength", "Dexterity", "Intelligence", "AllAttributes",
    ];

    public async Task<PobImportResult> ImportAsync(string codeOrUrl)
    {
        var result = new PobImportResult();

        if (string.IsNullOrWhiteSpace(codeOrUrl))
        {
            result.Error = resources["Import_Invalid_Code"];
            return result;
        }

        string xml;
        try
        {
            var (code, error) = await ResolveCodeAsync(codeOrUrl);
            if (error != null)
            {
                // 已经有针对性的失败原因（poe.ninja 那几条），直接采用，
                // 别再套一层 Import_Failed —— 否则用户看到的是"Import failed: ..."而不是原因。
                result.Error = error;
                return result;
            }

            xml = Inflate(code!);
        }
        catch (Exception ex)
        {
            result.Error = string.Format(CultureInfo.CurrentCulture, resources["Import_Failed"], ex.Message);
            return result;
        }

        try
        {
            return BuildTemplate(xml, codeOrUrl, result);
        }
        catch (Exception ex)
        {
            result.Error = string.Format(CultureInfo.CurrentCulture, resources["Import_Failed"], ex.Message);
            return result;
        }
    }

    /// <summary>
    /// 输入可能是 poe.ninja 角色页链接或 pobb.in 链接，先把它换成真正的分享码。
    /// 返回 (code, error)：error 非空表示已经备好用户可读的失败原因，调用方直接用它，
    /// 不要再包一层，否则那几条针对性提示会被"Import failed"盖掉。
    /// </summary>
    private async Task<(string? Code, string? Error)> ResolveCodeAsync(string codeOrUrl)
    {
        var trimmed = codeOrUrl.Trim();

        // poe.ninja 分支放在 pobb.in 之前：两者互不匹配，先判定更具体的链接没有副作用。
        if (PoeNinjaClient.TryParseCharacterUrl(trimmed, out var target))
        {
            var ninja = await poeNinja.ResolveExportCodeAsync(target);
            return ninja.Error == PoeNinjaError.None
                       ? (ninja.Code, null)
                       : (null, DescribeNinjaError(ninja.Error, ninja.Detail, target.League));
        }

        // 输入里明摆着是 poe.ninja，正则却没认出来（例如账号段带了未编码的 '#'）：说明是链接格式不对。
        // 绝不能掉进下面的"当成纯分享码"分支，否则用户看到的是 base64 报错，跟 poe.ninja 毫无关系。
        // 纯分享码和 pobb.in 链接都不含 poe.ninja，所以这道守卫不会误伤下面两条既有路径。
        if (MentionsPoeNinjaHost(trimmed))
        {
            return (null, resources["Import_Ninja_Bad_Link"]);
        }

        var match = PobbRegex.Match(trimmed);
        if (match.Success)
        {
            var raw = await HttpClient.GetStringAsync($"https://pobb.in/{match.Groups[1].Value}/raw");
            return (raw.Trim(), null);
        }

        return (trimmed, null);
    }

    /// <summary>
    /// 输入里是否出现 poe.ninja 主机名（忽略大小写）。
    /// 内部可见是为了离线断言"这条守卫只可能拦 poe.ninja 链接，不会碰到分享码 / pobb.in"。
    /// </summary>
    internal static bool MentionsPoeNinjaHost(string input) =>
        input.Contains("poe.ninja", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// poe.ninja 的失败原因 -&gt; 用户可读文案。
    /// 内部可见是为了能把这四条文案离线验一遍：真正走这个分支要联网。
    /// </summary>
    internal string DescribeNinjaError(PoeNinjaError error, string? detail, string league) =>
        error switch
        {
            PoeNinjaError.LeagueNotFound => string.Format(
                CultureInfo.CurrentCulture,
                resources["Import_Ninja_League_Unknown"].Value,
                league),
            PoeNinjaError.CharacterNotFound => resources["Import_Ninja_Character_Not_Found"].Value,
            PoeNinjaError.NoExportCode => resources["Import_Ninja_No_Export"].Value,
            PoeNinjaError.HttpError => string.Format(
                CultureInfo.CurrentCulture,
                resources["Import_Ninja_Failed"].Value,
                detail),
            _ => string.Empty,
        };

    /// <summary>PoB 分享码 = base64url(zlib(xml))。internal：测试与引擎侧都直接用它。</summary>
    internal static string Inflate(string code)
    {
        var normalized = code.Replace('-', '+').Replace('_', '/').Replace("\n", string.Empty).Replace("\r", string.Empty);
        normalized = normalized switch
        {
            _ when normalized.Length % 4 == 2 => normalized + "==",
            _ when normalized.Length % 4 == 3 => normalized + "=",
            _ => normalized,
        };

        var bytes = Convert.FromBase64String(normalized);
        using var input = new MemoryStream(bytes);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(zlib, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private PobImportResult BuildTemplate(string xml, string source, PobImportResult result)
    {
        var doc = XDocument.Parse(xml);

        // 物品 id -> 物品文本（XDocument 自动解码 XML 实体，并把内嵌 <ModRange> 的子文本一并并入，这里再剥掉标签）
        var items = new Dictionary<string, string>();
        foreach (var element in doc.Descendants("Item"))
        {
            var id = (string?)element.Attribute("id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                items[id] = element.Value;
            }
        }

        var template = new BuildTargetTemplate
        {
            Name = BuildName(doc),
            ImportedFrom = source.Length > 120 ? source[..120] : source,
            // BD 源码留着给 PoB 引擎当试穿基准（ImportedFrom 是截断过的，不能用来反推）
            PobXml = xml,
            // 导入的门槛一律是参考值，不是硬性要求（见 TargetNormalizer 的说明）。
            ImportedTargetsOptional = true,
        };

        // 角色合计（只统计能匹配上预设的词缀）
        var characterTotals = new Dictionary<string, (StatPresets.Preset Preset, double Value)>();

        foreach (var slot in doc.Descendants("Slot"))
        {
            var rawName = (string?)slot.Attribute("name") ?? string.Empty;
            var itemId = (string?)slot.Attribute("itemId") ?? "0";

            if (itemId == "0")
            {
                continue;
            }

            if (!SlotMap.TryGetValue(rawName, out var slotKey))
            {
                result.Notes.Add(string.Format(CultureInfo.CurrentCulture, resources["Import_Note_Skipped_Slot"], rawName));
                continue;
            }

            if (!items.TryGetValue(itemId, out var itemText))
            {
                continue;
            }

            var parsed = ParseItem(itemText);

            // 名字先记：兜底装备（匹配不到任何预设词缀）也要在界面上显示"对比对象"。
            template.EquippedNames[slotKey] = parsed.Name;

            if (parsed.Stats.Count == 0)
            {
                // 没有可识别词缀（Kalandra's Touch 这类独特装）：不写 Equipped / EquippedStats
                // ——写了会让评估器误判成"已采集"。只按部位推荐词缀建空门槛，用户自己填数。
                var fallback = new List<ModTarget>();
                foreach (var preset in StatPresets.ForSlot(slotKey).Take(4))
                {
                    fallback.Add(new ModTarget
                    {
                        Label = preset.Label,
                        Match = [.. preset.Keywords],
                        MinValue = 0,
                        Required = false,
                    });
                }

                template.Slots[slotKey] = fallback;
                result.Notes.Add(string.Format(CultureInfo.CurrentCulture, resources["Import_Note_Slot_Fallback"], rawName));
                continue;
            }

            // 快照：原文留着给用户看，数值单独存（客户端语言变了也能用）
            template.Equipped[slotKey] = parsed.NormalizedText;
            template.EquippedStats[slotKey] = parsed.Stats.ToDictionary(x => x.Key, x => x.Value);
            // 来源标记：这份基准是 BD 自带的，界面上和手动悬停采来的区分开
            template.EquippedSource[slotKey] = BaselineSources.Build;

            // 部位门槛：先按该部位的推荐顺序，再补上这件装备上有、但不在推荐表里的词缀。
            // 不能只扫推荐表 —— 搬砖号的装备常有取向特殊的词缀，漏掉就不是"照这套 BD 凑"了。
            var targets = new List<ModTarget>();
            foreach (var preset in StatPresets.ForSlot(slotKey).Concat(StatPresets.All).DistinctBy(x => x.Key))
            {
                if (!parsed.Stats.TryGetValue(preset.Label, out var value))
                {
                    continue;
                }

                targets.Add(new ModTarget
                {
                    Label = preset.Label,
                    Match = [.. preset.Keywords],
                    MinValue = value,
                    // ⚠ 一律非硬性。这里曾经按 RequiredKeys 把「生命/护盾/抗性」标成硬性门槛，
                    //   实际效果是「你必须穿得和这套 BD 一模一样」——用户把自己正穿的头盔
                    //   标为当前装备后仍被判「不建议」。硬性标准只留给用户自己在界面上勾。
                    Required = false,
                });
            }

            // 角色合计：按这件装备「识别到的全部词缀」累计。
            // 之前写在上面那个推荐表循环里，导致不在推荐表里的词缀不计入角色合计（对不上账）。
            foreach (var stat in parsed.Stats)
            {
                var preset = StatPresets.All.FirstOrDefault(x => x.Label == stat.Key);
                if (preset == null)
                {
                    continue;
                }

                var existing = characterTotals.TryGetValue(preset.Key, out var row)
                                   ? row
                                   : (Preset: preset, Value: 0d);
                characterTotals[preset.Key] = (preset, existing.Value + stat.Value);
            }

            template.Slots[slotKey] = targets;
        }

        if (template.Slots.Count == 0)
        {
            result.Error = resources["Import_No_Items"];
            return result;
        }

        // 角色总目标
        foreach (var key in CharacterKeys)
        {
            var preset = StatPresets.Find(key);
            if (preset == null || !characterTotals.TryGetValue(key, out var row) || row.Value <= 0)
            {
                continue;
            }

            template.Character.Add(new ModTarget
            {
                Label = preset.Label,
                Match = [.. preset.Keywords],
                MinValue = Math.Round(row.Value, 1),
                Required = false,
            });
        }

        template.UpdatedAt = DateTimeOffset.Now;

        result.Template = template;
        result.Notes.Add(string.Format(
            CultureInfo.CurrentCulture,
            resources["Import_Note_Summary"],
            template.Slots.Count,
            template.Character.Count));
        return result;
    }

    private static string BuildName(XDocument doc)
    {
        var build = doc.Descendants("Build").FirstOrDefault();
        var ascendancy = (string?)build?.Attribute("ascendClassName");
        var className = (string?)build?.Attribute("className");
        var level = (string?)build?.Attribute("level");

        var who = !string.IsNullOrWhiteSpace(ascendancy) ? ascendancy : className;
        if (string.IsNullOrWhiteSpace(who))
        {
            return "Imported BD";
        }

        return string.IsNullOrWhiteSpace(level) ? who : $"{who} Lv{level}";
    }

    private sealed class ParsedItem
    {
        public string Name { get; set; } = string.Empty;

        public string NormalizedText { get; set; } = string.Empty;

        /// <summary>预设 Label -&gt; 数值（同名词缀相加）。</summary>
        public Dictionary<string, double> Stats { get; set; } = [];
    }

    /// <summary>
    /// 解析 PoB 物品文本：剥掉标签/注解，跳过元数据与底子属性行，剩下来的就是词缀；
    /// 再按 Implicits 条数去掉固定词缀，最后按预设匹配取值。
    /// </summary>
    private static ParsedItem ParseItem(string rawText)
    {
        var text = System.Net.WebUtility.HtmlDecode(TagRegex.Replace(rawText, string.Empty));
        var lines = text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        var parsed = new ParsedItem
        {
            Name = lines.Count > 1 ? lines[1] : string.Empty,
            NormalizedText = text.Trim(),
        };

        // 词缀段起点：从头扫，遇到第一条真正的词缀就停。
        // 不能用"最后一条元数据行" —— Corrupted / Mirrored 之类出现在词缀之后。
        var start = 0;
        var implicitCount = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var low = lines[i].ToLowerInvariant();

            if (low.StartsWith("implicits:", StringComparison.Ordinal))
            {
                var nums = Regex.Match(low, @"\d+");
                implicitCount = nums.Success && int.TryParse(nums.Value, out var n) ? n : 0;
                start = i + 1;
                continue;
            }

            if (MetaPrefixes.Any(x => low.StartsWith(x, StringComparison.Ordinal)) || PropertyRegex.IsMatch(lines[i]))
            {
                start = i + 1;
                continue;
            }

            if (i < 3)
            {
                // 前 3 行固定是 Rarity / 物品名 / 底子名
                start = i + 1;
                continue;
            }

            break;
        }

        // 先按条数切掉固定词缀（implicits 就是头段之后的前 N 行，不管长什么样），
        // 再做注解剥离与元数据过滤 —— 顺序反了会跳过真实词缀。
        var body = lines.Skip(start).ToList();
        if (implicitCount > 0 && body.Count >= implicitCount)
        {
            body = body.Skip(implicitCount).ToList();
        }

        var mods = new List<string>();
        foreach (var line in body)
        {
            var bare = AnnotationRegex.Replace(line, string.Empty).Trim();
            if (bare.Length == 0)
            {
                continue;
            }

            var low = bare.ToLowerInvariant();
            if (MetaPrefixes.Any(x => low.StartsWith(x, StringComparison.Ordinal)) || PropertyRegex.IsMatch(bare))
            {
                continue;
            }

            mods.Add(bare);
        }

        foreach (var mod in mods)
        {
            var preset = MatchPreset(mod);
            if (preset == null)
            {
                continue;
            }

            var value = ExtractValue(mod);
            if (value == null)
            {
                continue;
            }

            parsed.Stats[preset.Label] = parsed.Stats.GetValueOrDefault(preset.Label) + value.Value;
        }

        return parsed;
    }

    private static StatPresets.Preset? MatchPreset(string line)
    {
        foreach (var preset in StatPresets.All)
        {
            foreach (var keyword in preset.Keywords)
            {
                // 词缀模板里的 `#` 代表数值，转成正则匹配
                var pattern = Regex.Escape(keyword).Replace("\\#", @"\d+(?:\.\d+)?");
                try
                {
                    if (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
                    {
                        return preset;
                    }
                }
                catch (ArgumentException)
                {
                    // 预设里的关键词理论上都合法，这里只是防御
                }
            }
        }

        return null;
    }

    /// <summary>取词缀数值：(10-15) 取中值；Adds 5 to 12 取均值；其余取第一个数字。</summary>
    private static double? ExtractValue(string line)
    {
        var range = RangeRegex.Match(line);
        if (range.Success &&
            double.TryParse(range.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var lo) &&
            double.TryParse(range.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var hi))
        {
            return Math.Round((lo + hi) / 2, 1);
        }

        var adds = AddsRegex.Match(line);
        if (adds.Success &&
            double.TryParse(adds.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var min) &&
            double.TryParse(adds.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var max))
        {
            return Math.Round((min + max) / 2, 1);
        }

        var number = NumberRegex.Match(line);
        return number.Success && double.TryParse(number.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                   ? Math.Round(value, 1)
                   : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TAO-BuildTarget/1.0");
        return client;
    }
}
