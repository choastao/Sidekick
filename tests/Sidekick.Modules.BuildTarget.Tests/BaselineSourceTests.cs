using System.Globalization;
using System.IO.Compression;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;
using Sidekick.Modules.BuildTarget.Localization;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 当前装备基准的来源（EquippedSource）：
/// 导入 BD = bd、游戏内悬停采集 = manual、老配置没这个字段 = 来源未知（不许猜）。
/// 老配置文件里没有 equippedSource 是必须能正常跑的第一条前提，所以这里从 JSON 开始测。
/// </summary>
public class BaselineSourceTests
{
    /// <summary>和 BuildTargetStore 里一致的落盘选项。</summary>
    private static readonly JsonSerializerOptions StoreJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ---- 验收：老配置文件（没有 equippedSource）加载后行为 ----

    [Fact]
    public void Old_config_without_equipped_source_loads_and_counts_as_unknown()
    {
        // helmet 是旧版导入的 BD：只有抽好的数值，没有来源标记
        // gloves 是旧版手动采集：只有物品原文（这里假定能解析出来）
        const string json = """
        {
          "version": 1,
          "activeId": "a1",
          "templates": [
            {
              "id": "a1",
              "name": "老模板",
              "character": [],
              "slots": {},
              "equipped": {
                "helmet": "Rarity: RARE\nGloom Cowl\nHubris Circlet",
                "gloves": "物品类别: 手套\n稀有度: 稀有\n旧手套\n术士手套"
              },
              "equippedStats": {
                "helmet": { "最大生命": 90 }
              },
              "equippedNames": {
                "helmet": "Gloom Cowl",
                "gloves": "旧手套"
              },
              "importedFrom": null,
              "updatedAt": "2026-01-01T00:00:00+08:00"
            }
          ]
        }
        """;

        var file = JsonSerializer.Deserialize<BuildTargetFile>(json, StoreJsonOptions);
        var template = Assert.Single(file!.Templates);

        // 反序列化后不能是 null：老配置里根本没有这个字段
        Assert.NotNull(template.EquippedSource);
        Assert.Empty(template.EquippedSource);

        // 老数据能继续写来源标记，不会 NRE
        template.EquippedSource[SlotKeys.Helmet] = BaselineSources.Manual;
        Assert.Equal(BaselineSources.Manual, template.EquippedSource[SlotKeys.Helmet]);
        template.EquippedSource.Remove(SlotKeys.Helmet);

        var summary = BaselineSummaryCalculator.Compute(template, slotKey => slotKey == SlotKeys.Gloves);

        Assert.Equal(SlotKeys.All.Length, summary.TotalSlots);
        Assert.Equal(2, summary.WithBaseline);
        Assert.Equal(0, summary.FromBuild);
        Assert.Equal(0, summary.Manual);
        Assert.Equal(2, summary.Unknown); // 有基准值、没来源标记 → 兜底「来源未知」
    }

    [Fact]
    public void Null_equipped_source_in_json_is_tolerated()
    {
        const string json = """
        {
          "id": "a1",
          "name": "template",
          "equippedSource": null,
          "equippedStats": { "helmet": { "最大生命": 90 } }
        }
        """;

        var template = JsonSerializer.Deserialize<BuildTargetTemplate>(json, StoreJsonOptions)!;

        Assert.NotNull(template.EquippedSource);
        Assert.Empty(template.EquippedSource);

        // 写进空字典再序列化，读回来还是原样
        template.EquippedSource[SlotKeys.Helmet] = BaselineSources.Build;
        var roundTrip = JsonSerializer.Deserialize<BuildTargetTemplate>(
            JsonSerializer.Serialize(template, StoreJsonOptions),
            StoreJsonOptions)!;
        Assert.Equal(BaselineSources.Build, roundTrip.EquippedSource[SlotKeys.Helmet]);
    }

    // ---- 验收：导入 BD 后基准来源是 bd ----

    [Fact]
    public async Task Imported_build_marks_recorded_slot_as_bd()
    {
        var result = await new PobBuildImporter(new PoeNinjaClient(), new TestLocalizer()).ImportAsync(BuildShareCode());

        Assert.Null(result.Error);
        var template = Assert.IsType<BuildTargetTemplate>(result.Template);

        // 导入时把 BD 自带装备写进了基准，来源标记为 bd
        Assert.True(template.EquippedStats.ContainsKey(SlotKeys.Helmet));
        Assert.Equal(BaselineSources.Build, template.EquippedSource[SlotKeys.Helmet]);
        Assert.Equal(2, template.EquippedStats[SlotKeys.Helmet].Count); // 最大生命 + 火焰抗性

        // BD 里没穿（itemId=0）的部位不会被写成有基准
        Assert.False(template.EquippedSource.ContainsKey(SlotKeys.Gloves));
        Assert.False(template.EquippedStats.ContainsKey(SlotKeys.Gloves));

        var summary = BaselineSummaryCalculator.Compute(template, _ => false);
        Assert.Equal(1, summary.WithBaseline);
        Assert.Equal(1, summary.FromBuild);
        Assert.Equal(0, summary.Manual);
        Assert.Equal(0, summary.Unknown);
    }

    // ---- 验收：手动采集标记成 manual，并且覆盖 BD 的来源 ----

    [Fact]
    public void Manual_capture_overrides_build_source()
    {
        var template = new BuildTargetTemplate
        {
            Equipped = { [SlotKeys.Helmet] = "Rarity: RARE\nGloom Cowl\nHubris Circlet" },
            EquippedStats = { [SlotKeys.Helmet] = new Dictionary<string, double> { ["最大生命"] = 84 } },
            EquippedSource = { [SlotKeys.Helmet] = BaselineSources.Build },
        };

        // BuildTargetStore.CaptureEquipped 调的就是这个方法。
        // 传 parsed: null 模拟「快照解析不出来」——这时必须把导入时抽的数值一起清掉。
        BaselineSources.MarkManual(template, SlotKeys.Helmet, "物品类别: 头盔\n稀有度: 稀有\n我的头盔\n灵主之冠", parsed: null);

        Assert.Equal(BaselineSources.Manual, template.EquippedSource[SlotKeys.Helmet]);
        Assert.Equal("物品类别: 头盔\n稀有度: 稀有\n我的头盔\n灵主之冠", template.Equipped[SlotKeys.Helmet]);

        // 关键：不能留着 BD 那件的数值和名字。否则快照一解析失败，
        // 评估会拿 BD 的数值冒充「你的当前装备」，静默给出错误结论。
        Assert.False(template.EquippedStats.ContainsKey(SlotKeys.Helmet));
        Assert.False(template.EquippedNames.ContainsKey(SlotKeys.Helmet));

        // 同一个部位只能算一次，算到 manual 里，不再算 bd
        var summary = BaselineSummaryCalculator.Compute(template, slotKey => slotKey == SlotKeys.Helmet);
        Assert.Equal(1, summary.WithBaseline);
        Assert.Equal(1, summary.Manual);
        Assert.Equal(0, summary.FromBuild);
        Assert.Equal(0, summary.Unknown);
    }

    [Fact]
    public void Summary_counts_each_bucket_and_skips_slots_without_baseline()
    {
        var template = new BuildTargetTemplate
        {
            // 导入的 BD：有数值 + bd 标记
            EquippedStats =
            {
                [SlotKeys.Helmet] = new Dictionary<string, double> { ["最大生命"] = 80 },
                [SlotKeys.Boots] = new Dictionary<string, double> { ["最大生命"] = 70 },
            },
            EquippedSource =
            {
                [SlotKeys.Helmet] = BaselineSources.Build,
                [SlotKeys.Gloves] = BaselineSources.Manual,
            },
            // 手动采的（原文在 Equipped 里，能否解析由调用方判断）
            Equipped =
            {
                [SlotKeys.Gloves] = "游戏里采到的手套原文",
                [SlotKeys.Belt] = "语言对不上的腰带原文",
            },
        };

        // boots 有数值没来源标记（老数据）；gloves 能解析；belt 解析不出来
        var summary = BaselineSummaryCalculator.Compute(template, slotKey => slotKey == SlotKeys.Gloves);

        Assert.Equal(SlotKeys.All.Length, summary.TotalSlots);
        Assert.Equal(3, summary.WithBaseline);
        Assert.Equal(1, summary.FromBuild);
        Assert.Equal(1, summary.Manual);
        Assert.Equal(1, summary.Unknown);

        // 解析不出来的部位不算有基准
        Assert.Equal(3, summary.FromBuild + summary.Manual + summary.Unknown);
    }

    // ---- 文案：中英两份都要有，占位符要一致 ----

    [Fact]
    public void Baseline_sentence_shows_the_source_breakdown()
    {
        var chinese = new TestLocalizer(CultureInfo.GetCultureInfo("zh"));
        var english = new TestLocalizer(CultureInfo.InvariantCulture);

        // 导入的 BD 给 6 个部位、手动补了 2 个（用户就是被「1/10」误导的）
        var summary = new BaselineSummary { WithBaseline = 8, FromBuild = 6, Manual = 2 };
        Assert.Equal("10 个部位中 8 个有基准（6 个来自导入的 BD，2 个手动采集）。", BaselineNoteFormatter.Format(summary, chinese));
        Assert.Equal("8 of 10 slots have a baseline (6 from the imported build, 2 captured in game).", BaselineNoteFormatter.Format(summary, english));

        // 老数据：有基准值但没有来源标记 → 兜底「来源未知」，不猜
        var withUnknown = new BaselineSummary { WithBaseline = 9, FromBuild = 6, Manual = 2, Unknown = 1 };
        Assert.Equal("10 个部位中 9 个有基准（6 个来自导入的 BD，2 个手动采集，1 个来源未知）。", BaselineNoteFormatter.Format(withUnknown, chinese));
        Assert.Equal("9 of 10 slots have a baseline (6 from the imported build, 2 captured in game, 1 of unknown source).", BaselineNoteFormatter.Format(withUnknown, english));

        // 一个基准都没有
        Assert.Equal("10 个部位还没有基准。", BaselineNoteFormatter.Format(new BaselineSummary(), chinese));
        Assert.Equal("No baseline yet for any of the 10 slots.", BaselineNoteFormatter.Format(new BaselineSummary(), english));
    }

    [Fact]
    public void Source_labels_cover_bd_manual_and_old_data()
    {
        var chinese = new TestLocalizer(CultureInfo.GetCultureInfo("zh"));

        Assert.Equal("来自导入的 BD", BaselineNoteFormatter.SourceLabel(BaselineSources.Build, chinese));
        Assert.Equal("手动采集", BaselineNoteFormatter.SourceLabel(BaselineSources.Manual, chinese));
        Assert.Equal("来源未知", BaselineNoteFormatter.SourceLabel(null, chinese));
        Assert.Equal("来源未知", BaselineNoteFormatter.SourceLabel("something-else", chinese));
    }

    [Fact]
    public void Baseline_strings_exist_in_both_languages_with_matching_placeholders()
    {
        var manager = new ResourceManager(
            "Sidekick.Modules.BuildTarget.Localization.BuildTargetResources",
            typeof(BuildTargetResources).Assembly);

        string[] keys =
        [
            "Estimate_Note",
            "Baseline_Summary",
            "Baseline_Summary_None",
            "Baseline_From_Bd",
            "Baseline_Manual",
            "Baseline_Unknown",
            "Baseline_List_Separator",
            "Baseline_Import_Note",
            "Baseline_Source_Bd",
            "Baseline_Source_Manual",
            "Baseline_Source_Unknown",
            "Equipped_Help",
            "No_Baseline_Hint",
        ];

        foreach (var key in keys)
        {
            var english = manager.GetString(key, CultureInfo.InvariantCulture);
            var chinese = manager.GetString(key, CultureInfo.GetCultureInfo("zh"));

            Assert.False(string.IsNullOrEmpty(english), $"{key} 缺少英文文案");
            Assert.False(string.IsNullOrEmpty(chinese), $"{key} 缺少中文文案");
            Assert.Equal(Signature(english!), Signature(chinese!));

            // 界面上不许出现旧名字
            Assert.DoesNotContain("Sidekick", english!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sidekick", chinese!, StringComparison.OrdinalIgnoreCase);

            var formatted = string.Format(
                CultureInfo.InvariantCulture,
                english!,
                Enumerable.Range(0, Signature(english!))
                          .Select(index => (object)(index + 1))
                          .ToArray());

            Assert.DoesNotContain("{", formatted, StringComparison.Ordinal);
        }

        // 说明句必须点明「导入一次就有基准，不用手动悬停」
        var note = manager.GetString("Baseline_Import_Note", CultureInfo.GetCultureInfo("zh"))!;
        Assert.Contains("导入一次就有基准", note, StringComparison.Ordinal);
        Assert.Contains("不用手动悬停", note, StringComparison.Ordinal);

        // 引导手动采集的文案不许再写成「每个部位都要悬停」
        var help = manager.GetString("Equipped_Help", CultureInfo.GetCultureInfo("zh"))!;
        Assert.Contains("缺", help, StringComparison.Ordinal);
        Assert.DoesNotContain("每一件", help, StringComparison.Ordinal);
        Assert.DoesNotContain("逐个", help, StringComparison.Ordinal);
    }

    /// <summary>文案里用到的参数个数（最大占位符下标 + 1）。</summary>
    private static int Signature(string format)
    {
        var matches = Regex.Matches(format, @"\{(\d+)");
        return matches.Count == 0 ? 0 : matches.Max(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)) + 1;
    }

    /// <summary>一份最小的 PoB2 分享码（base64url(zlib(xml))），不走网络。</summary>
    private static string BuildShareCode()
    {
        const string xml = """
        <PathOfBuilding>
          <Build level="92" className="Ranger" ascendClassName="Deadeye" />
          <Items>
            <Item id="1">Rarity: RARE
        Gloom Cowl
        Hubris Circlet
        Quality: 20
        Armour: 214
        +84 to maximum Life
        +42% to Fire Resistance
        </Item>
          </Items>
          <Slots>
            <Slot name="Helmet" itemId="1" />
            <Slot name="Gloves" itemId="0" />
          </Slots>
        </PathOfBuilding>
        """;

        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(xml);
            zlib.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

}
