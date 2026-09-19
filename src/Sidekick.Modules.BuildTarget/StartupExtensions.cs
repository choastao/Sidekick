using Microsoft.Extensions.DependencyInjection;
using Sidekick.Common;
using Sidekick.Common.Settings;
using Sidekick.Modules.BuildTarget.Keybinds;
using Sidekick.Modules.BuildTarget.Services;

namespace Sidekick.Modules.BuildTarget;

/// <summary>
/// 目标 BD 评估模块的启动配置。
/// </summary>
public static class StartupExtensions
{
    public static IServiceCollection AddSidekickBuildTarget(this IServiceCollection services)
    {
        services.AddSidekickModule(typeof(StartupExtensions).Assembly);

        services.AddSingleton<BuildTargetStore>();
        services.AddSingleton<BuildTargetEvaluator>();
        services.AddSingleton<PobBuildImporter>();
        services.AddSingleton<CurrencyPriceService>();
        services.AddSingleton<AffixTypeService>();
        services.AddSingleton<AffixWeightService>();

        // 备选篮：内存态 + candidate-basket.json
        services.AddSingleton<CandidateBasketService>();
        services.AddSidekickInputHandler<AddToBasketKeybindHandler>();

        // 默认热键 Ctrl+B（B = Basket）。挑它的原因见 AddToBasketKeybindHandler 的注释：
        // 不和 Ctrl+D / Ctrl+F / Alt+W / Space，以及多开窗口2 的 Ctrl+Shift+D 撞车。
        services.SetSidekickDefaultSetting(SettingKeys.KeyAddToBasket, "Ctrl+B");

        return services;
    }
}
