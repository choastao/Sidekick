using Microsoft.Extensions.Logging;
using Sidekick.Common.Platform;
using Sidekick.Common.Settings;
using Sidekick.Common.Settings.Input;
using Sidekick.Game.Parser;
using Sidekick.Game.Parser.Items;
using Sidekick.Modules.BuildTarget.Models;
using Sidekick.Modules.BuildTarget.Services;

namespace Sidekick.Modules.BuildTarget.Keybinds;

/// <summary>
/// 「把鼠标悬停的物品标记为备选」快捷键。
///
/// 行为：复制悬停物品 → 解析 → 只取它的抗性 / 属性贡献存进备选篮。
/// 有意不弹任何窗口：连续标记几件时窗口抢焦点会让下一次快捷键按不出来，
/// 所以这里静默入篮（写日志），物品悬浮窗的「备选篮」页签上会显示件数。
///
/// 默认键 Ctrl+B（B = Basket）：和现有默认键 Ctrl+D（查价）、Ctrl+F（查找物品）、Alt+W（wiki）、
/// Space（关闭悬浮窗）都不冲突，也不撞多开时窗口2 的查价键 Ctrl+Shift+D。
/// </summary>
public class AddToBasketKeybindHandler(
    IClipboardProvider clipboardProvider,
    ISettingsService settingsService,
    IProcessProvider processProvider,
    ItemParser itemParser,
    CandidateBasketService basketService,
    IInputProvider input,
    ILogger<AddToBasketKeybindHandler> logger) : KeybindHandler(settingsService, SettingKeys.KeyAddToBasket)
{
    private readonly ISettingsService settingsService = settingsService;

    protected override async Task<List<string?>> GetKeybinds() =>
    [
        await settingsService.GetString(SettingKeys.KeyAddToBasket)
    ];

    public override bool IsValid(string _) => processProvider.IsPathOfExileInFocus;

    public override async Task Execute(string keybind)
    {
        // withAlt: 带上物品类别等信息（PoE1 的类别行只在进阶复制里有），解析器两种格式都吃
        var itemText = await clipboardProvider.Copy(withAlt: true);
        if (itemText == null)
        {
            await input.PressKey(keybind);
            return;
        }

        Item item;
        try
        {
            item = itemParser.ParseItem(itemText);
        }
        catch (Exception ex)
        {
            // 鼠标下面不是物品 / 复制到的文本解析不了：什么都不做，也不打扰用户
            logger.LogWarning(ex, "[Basket] Could not parse the hovered item.");
            return;
        }

        var slotKey = SlotKeys.ResolveFor(item).FirstOrDefault() ?? SlotKeys.Unknown;
        var entry = basketService.Add(item, slotKey);
        logger.LogInformation(
            "[Basket] Added {Name} ({Slot}) - {Count} candidate(s) in the basket.",
            entry.Name,
            entry.SlotKey,
            basketService.Items.Count);
    }
}
