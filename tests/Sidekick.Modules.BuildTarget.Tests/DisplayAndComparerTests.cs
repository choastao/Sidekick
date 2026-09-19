using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;
using Xunit;

namespace Sidekick.Modules.BuildTarget.Tests;

/// <summary>
/// 「换装后」这一格的显示决策，和「这件是不是就是你现在穿的那件」的文本比对。
///
/// 这两块原本写在 Razor 组件/评估器私有方法里，删掉它们不会有任何测试变红
/// （CC 审计发现：「本轮唯一新加的运行时判定，恰好落在测试盲区里」）。
/// 所以抽成可测的纯函数并在这里锁住。
/// </summary>
public class DisplayAndComparerTests
{
    // ---- 是否就是当前装备：两边必须过同样的清洗后才能比 ----

    [Fact]
    public void Same_item_with_messy_whitespace_still_matches()
    {
        const string stored = "物品種類: 頭部\r\n稀有度: 傳奇\r\n微笑騎士\r\n\r\n斗篷之盔\r\n";

        // 快照存的是原始粘贴文本；被评估的物品文本已被 OriginalText 清洗过。
        // 换行、空行、行首尾空白的差异都不能影响判定。
        Assert.True(ItemTextComparer.AreSame(stored, "物品種類: 頭部\n稀有度: 傳奇\n微笑騎士\n斗篷之盔"));
    }

    [Fact]
    public void Text_containing_bracket_markers_compares_consistently()
    {
        // 不猜 OriginalText 的清洗细节，只锁住真正重要的性质：
        // 两边走同一套清洗 → 自身必定相等；只有空白差异（缩进/空行/行尾）也必须相等。
        const string withBrackets = "Item Class: Helmets\nRarity: Rare\nSmiling Knight [1]\nCloak Crown";
        const string sameWithWhitespace = "Item Class: Helmets\r\nRarity: Rare\r\n  Smiling Knight [1]  \r\n\r\nCloak Crown\r\n";

        Assert.True(ItemTextComparer.AreSame(withBrackets, withBrackets));
        Assert.True(ItemTextComparer.AreSame(withBrackets, sameWithWhitespace));
    }

    [Fact]
    public void Different_rolls_of_the_same_unique_are_not_the_same_item()
    {
        // 同一件独特装的不同 roll（数值不同）绝不能被判成同一件，
        // 否则会告诉用户「这就是你现在穿的」，而其实是另一件。
        const string stored = "物品種類: 頭部\n稀有度: 傳奇\n微笑騎士\n護甲值: 109\n閃避值: 95";
        const string other = "物品種類: 頭部\n稀有度: 傳奇\n微笑騎士\n護甲值: 130\n閃避值: 114";

        Assert.False(ItemTextComparer.AreSame(stored, other));
    }

    [Theory]
    [InlineData(null, "abc")]
    [InlineData("abc", null)]
    [InlineData("", "")]
    [InlineData("   ", "abc")]
    public void Empty_or_missing_text_is_never_a_match(string? a, string? b)
    {
        Assert.False(ItemTextComparer.AreSame(a, b));
    }

    // ---- 「换装后」这一格：显示什么、什么颜色 ----

    private static ModCheck Check(double? newValue, bool required) => new()
    {
        Label = "最大能量护盾",
        MinValue = 58,
        Required = required,
        New = newValue,
    };

    [Fact]
    public void A_stat_the_item_does_not_provide_is_shown_as_an_em_dash()
    {
        // 参考值：灰的「—」——「没有这条」不等于「不达标」
        Assert.Equal(StatDisplay.NotProvided, StatDisplay.Value(Check(null, required: false)));
        Assert.Equal("text-stone-500", StatDisplay.Class(Check(null, required: false)));

        // 但用户自己勾了硬性要求时，装备没有这条就是未达标 → 必须标红。
        // 否则会出现「同一屏上判了不建议、这一格却是灰的」自相矛盾（CC 审计发现）。
        Assert.Equal(StatDisplay.NotProvided, StatDisplay.Value(Check(null, required: true)));
        Assert.Equal("text-red-400", StatDisplay.Class(Check(null, required: true)));
    }

    [Fact]
    public void Colour_follows_whether_the_target_is_required()
    {
        Assert.Equal("", StatDisplay.Class(Check(70, required: false)));            // 达标
        Assert.Equal("text-amber-400", StatDisplay.Class(Check(30, required: false))); // 参考值没到
        Assert.Equal("text-red-400", StatDisplay.Class(Check(30, required: true)));    // 硬性没到
        Assert.Equal("70", StatDisplay.Value(Check(70, required: false)));
    }
}
