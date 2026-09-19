using Microsoft.Extensions.DependencyInjection;
using Sidekick.Common;
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

        return services;
    }
}
