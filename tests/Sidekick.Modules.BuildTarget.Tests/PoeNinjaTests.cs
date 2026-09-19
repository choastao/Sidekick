using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;
using Xunit.Abstractions;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// poe.ninja 角色页导入：链接解析、index-state 映射、以及"夹具里的导出码能端到端解出模板"。
/// 全程离线 —— 夹具就是真实接口响应的快照。
/// </summary>
public class PoeNinjaTests(ITestOutputHelper output)
{
    private const string RealUrl =
        "https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC?i=2&search=class%3DBlood%2BMage";

    // ---- 1. 真实链接正例 ----

    [Fact]
    public void Parses_real_character_url_into_league_account_character()
    {
        Assert.True(PoeNinjaClient.TryParseCharacterUrl(RealUrl, out var target));
        Assert.Equal("forbiddenrites", target.League);
        Assert.Equal("IMBigcousin-6756", target.Account);
        Assert.Equal("CN_FEC", target.Character);
    }

    // ---- 2. 形态变体 ----

    [Theory]
    [InlineData("https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC/")]
    [InlineData("https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC#equipment")]
    [InlineData("poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC")]
    [InlineData("https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC/?i=2#equipment")]
    public void Parses_character_url_variants(string input)
    {
        Assert.True(PoeNinjaClient.TryParseCharacterUrl(input, out var target), $"应能解析：{input}");
        Assert.Equal("forbiddenrites", target.League);
        Assert.Equal("IMBigcousin-6756", target.Account);
        Assert.Equal("CN_FEC", target.Character);
    }

    [Fact]
    public void Decodes_url_encoded_account_name()
    {
        // %23 是 '#'：账号名带井号时必须解码，否则拿原串去打接口必然查不到。
        const string input = "https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin%236756/CN_FEC";

        Assert.True(PoeNinjaClient.TryParseCharacterUrl(input, out var target));
        Assert.Equal("IMBigcousin#6756", target.Account);
    }

    // ---- 2b. 粘贴污染：尾随标点必须被剥掉，不能混进角色名（否则会假报"角色不存在"）----

    [Theory]
    [InlineData("https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC.")]
    [InlineData("https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC,")]
    [InlineData("https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC」")]
    [InlineData("https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC。")]
    [InlineData("https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC).")]
    [InlineData("  https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC.  ")]
    public void Strips_trailing_punctuation_from_pasted_url(string input)
    {
        Assert.True(PoeNinjaClient.TryParseCharacterUrl(input, out var target), $"应能解析：{input}");
        Assert.Equal("forbiddenrites", target.League);
        Assert.Equal("IMBigcousin-6756", target.Account);
        // 关键：角色名不带尾随标点 —— 否则会拿着 "CN_FEC." 去打接口，回 404 变成假报"角色不存在"。
        Assert.Equal("CN_FEC", target.Character);
    }

    // ---- 2c. 尾随标点剥离不能伤到既有形态（?query / #fragment / 结尾斜杠 / %23 编码）----

    [Theory]
    [InlineData("https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC?i=2&search=class%3DBlood%2BMage")]
    [InlineData("https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC#equipment")]
    [InlineData("https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC/")]
    [InlineData("poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC")]
    public void Trailing_punctuation_trim_keeps_existing_url_forms(string input)
    {
        Assert.True(PoeNinjaClient.TryParseCharacterUrl(input, out var target), $"应能解析：{input}");
        Assert.Equal("CN_FEC", target.Character);
    }

    [Fact]
    public void Trailing_punctuation_trim_keeps_encoded_account()
    {
        // %23 结尾形态：剥离表里没有 '%'，编码账号必须原样保住。
        const string input = "https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin%236756/CN_FEC";

        Assert.True(PoeNinjaClient.TryParseCharacterUrl(input, out var target));
        Assert.Equal("IMBigcousin#6756", target.Account);
        Assert.Equal("CN_FEC", target.Character);
    }

    // ---- 3. 反例：这些都不能命中，否则会抢走既有的导入路径 ----

    [Theory]
    [InlineData("eNrtPWtz8kC3P3jBH5K704mZd3IuZgYzA8jCzAJmZgYzA8jCzAJmZgYzA8jCzAJmZgYzA8jCzAJmZgYzA")]
    [InlineData("https://pobb.in/xxxx")]
    [InlineData("")]
    [InlineData("https://example.com/poe2/builds/forbiddenrites/character/IMBigcousin-6756/CN_FEC")]
    [InlineData("https://poe.ninja/poe2/economy/forbiddenrites/currency")]
    public void Rejects_non_character_urls(string input)
    {
        Assert.False(PoeNinjaClient.TryParseCharacterUrl(input, out var target), $"不该命中：{input}");
        Assert.Null(target);
    }

    // ---- 4. index-state 映射：url 与 snapshotName 确实不是一个串 ----

    [Fact]
    public void Index_state_maps_league_url_to_version_and_snapshot_name()
    {
        var map = PoeNinjaClient.ParseIndexState(ReadFixture("poeninja-index-state.json"));

        Assert.True(map.TryGetValue("forbiddenrites", out var entry), "index-state 里应有 forbiddenrites");

        output.WriteLine($"forbiddenrites -> version={entry.Version} snapshotName={entry.SnapshotName}");

        // 夹具里 forbiddenrites 的 version（version 每天轮换，这里以夹具快照为准）。
        Assert.Equal("1650-20260919-45662", entry.Version);
        Assert.Equal("forbidden-rites", entry.SnapshotName);
        // 关键不变量：链接用的是 url（无连字符），overview 参数要用 snapshotName（有连字符），两者不同。
        Assert.NotEqual("forbiddenrites", entry.SnapshotName);
    }

    // ---- 5. 端到端离线：夹具里的 pathOfBuildingExport 直接喂进导入逻辑 ----

    [Fact]
    public async Task Fixture_export_code_imports_end_to_end_without_network()
    {
        var code = ExtractExportCode();

        // 走的是纯分享码路径（不含 pobb.in / poe.ninja），不触发任何网络。
        var importer = new PobBuildImporter(new PoeNinjaClient(), new TestLocalizer());
        var result = await importer.ImportAsync(code);

        Assert.Null(result.Error);
        var template = Assert.IsType<BuildTargetTemplate>(result.Template);

        Assert.False(string.IsNullOrWhiteSpace(template.Name));
        Assert.Equal(10, template.Slots.Count);

        // 10 个跟踪部位都要有门槛（哪怕是兜底推荐词缀）。
        foreach (var slot in SlotKeys.All)
        {
            Assert.True(template.Slots.ContainsKey(slot), $"缺少跟踪部位 {slot}");
        }

        // Ring 2 是 Kalandra's Touch（物品文本里只有 "Reflects opposite Ring"），
        // 一名词缀都识别不出来 —— 导入器按既有规则走兜底分支：只建推荐词缀门槛，不写 EquippedStats。
        // 所以这份导出码是「10 个部位都有装备，9 个有数值」，差的那一个就是它。
        Assert.DoesNotContain(SlotKeys.Ring2, template.EquippedStats.Keys);

        var withStats = SlotKeys.All.Where(x => template.EquippedStats.ContainsKey(x)).ToList();
        Assert.Equal(9, withStats.Count);

        foreach (var slot in withStats)
        {
            Assert.NotEmpty(template.EquippedStats[slot]);
        }

        output.WriteLine(
            $"Slots={template.Slots.Count} EquippedStats={template.EquippedStats.Count} Name={template.Name}");
        output.WriteLine("EquippedStats 明细：" + string.Join(
            ", ",
            SlotKeys.All.Select(x => $"{x}={(template.EquippedStats.TryGetValue(x, out var s) ? s.Count.ToString() : "无(兜底)")}")));
    }

    // ---- 四种失败都要给到"那四条"针对性文案，不能被 Import_Failed 兜底盖掉 ----

    [Theory]
    [InlineData(PoeNinjaError.LeagueNotFound, "Import_Ninja_League_Unknown")]
    [InlineData(PoeNinjaError.CharacterNotFound, "Import_Ninja_Character_Not_Found")]
    [InlineData(PoeNinjaError.NoExportCode, "Import_Ninja_No_Export")]
    [InlineData(PoeNinjaError.HttpError, "Import_Ninja_Failed")]
    public void Ninja_failures_surface_their_own_message(PoeNinjaError error, string resourceKey)
    {
        const string league = "forbiddenrites";
        const string detail = "HTTP 429，Retry-After 30s";

        var localizer = new TestLocalizer();
        var importer = new PobBuildImporter(new PoeNinjaClient(), localizer);

        var message = importer.DescribeNinjaError(error, detail, league);

        var template = localizer[resourceKey].Value;
        // TestLocalizer 缺键会回退成键名：这一条同时守着「资源真的存在」。
        Assert.NotEqual(resourceKey, template);

        var expected = template.Contains("{0}", StringComparison.Ordinal)
                           ? string.Format(
                               CultureInfo.CurrentCulture,
                               template,
                               error == PoeNinjaError.LeagueNotFound ? league : detail)
                           : template;

        Assert.Equal(expected, message);

        // 兜底文案是 "Import failed: …" / "导入失败：…"：这四条文案里绝不能带上它。
        var genericWrapper = localizer["Import_Failed"].Value.Replace("{0}", string.Empty).Trim();
        Assert.DoesNotContain(genericWrapper, message, StringComparison.Ordinal);
    }

    // ---- 6. 含 poe.ninja 却认不出的链接：报"格式不对"，不能掉进 base64 分支 ----

    [Fact]
    public async Task Unrecognised_ninja_link_surfaces_bad_link_message_instead_of_base64_error()
    {
        // 账号段里是未编码的 '#'（应为 %23）：正则认不出来，但输入里明明有 poe.ninja。
        const string badLink = "https://poe.ninja/poe2/builds/forbiddenrites/character/IMBigcousin#6756/CN_FEC";

        Assert.False(PoeNinjaClient.TryParseCharacterUrl(badLink, out _));
        Assert.True(PobBuildImporter.MentionsPoeNinjaHost(badLink));

        var localizer = new TestLocalizer();
        var importer = new PobBuildImporter(new PoeNinjaClient(), localizer);

        var result = await importer.ImportAsync(badLink);

        // 走的是新资源键的文案（TestLocalizer 缺键会回退成键名，所以这一条同时守着"资源存在"）。
        Assert.NotEqual("Import_Ninja_Bad_Link", localizer["Import_Ninja_Bad_Link"].Value);
        Assert.Equal(localizer["Import_Ninja_Bad_Link"].Value, result.Error);

        // 这正是被修掉的症状：以前会掉进 base64 分支，报 "The input is not a valid Base-64 string …"。
        Assert.DoesNotContain("Base-64", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Template);
    }

    // ---- 7. 守卫的负例：既有的两条导入路径不可能被它拦到 ----

    [Theory]
    [InlineData("https://pobb.in/xxxx")]
    [InlineData("eNrtPWtz8kC3P3jBH5K704mZd3IuZgYzA8jCzAJmZgYzA8jCzAJmZgYzA8jCzAJmZgYzA8jCzAJmZgYzA")]
    public void Poe_ninja_guard_does_not_catch_pobb_or_share_code(string input)
    {
        Assert.False(PobBuildImporter.MentionsPoeNinjaHost(input), $"不该被 poe.ninja 守卫拦：{input}");
    }

    [Fact]
    public async Task Pure_share_code_still_takes_the_base64_path()
    {
        var code = ExtractExportCode();

        Assert.False(PobBuildImporter.MentionsPoeNinjaHost(code));

        var importer = new PobBuildImporter(new PoeNinjaClient(), new TestLocalizer());
        var result = await importer.ImportAsync(code);

        // 守卫没拦它：继续走 base64 解压并成功建出模板（若被拦，这里会是 Bad_Link 文案、Template 为 null）。
        Assert.Null(result.Error);
        Assert.NotNull(result.Template);
    }

    private static string ExtractExportCode()
    {
        using var doc = JsonDocument.Parse(ReadFixture("poeninja-character.json"));
        var code = doc.RootElement.GetProperty("pathOfBuildingExport").GetString();
        Assert.False(string.IsNullOrWhiteSpace(code), "夹具里应有 pathOfBuildingExport");
        return code!;
    }

    /// <summary>夹具在测试源码目录下，用调用方文件路径定位，和当前工作目录无关。</summary>
    private static string ReadFixture(string name, [CallerFilePath] string thisFile = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(thisFile)!, "Fixtures", name));
}
