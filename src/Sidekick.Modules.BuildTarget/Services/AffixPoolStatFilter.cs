using Microsoft.Extensions.Logging;
using Sidekick.Game;
using Sidekick.Game.Languages.Implementations;
using Sidekick.Game.Providers;
using Sidekick.Game.TradeStats;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 词缀搜索器的「按部位过滤」：给一个标签集，回答「这条交易站词缀在这个部位能不能出」。
///
/// 索引（<see cref="AffixPoolIndex"/>）是惰性建的：只有真的要用过滤时才去读英文词缀库，
/// 读不到就返回 null —— 调用方必须降级成「不过滤」，绝不能当成空池把词缀全过滤掉。
/// </summary>
public class AffixPoolStatFilter
{
    private readonly AffixWeightService weights;
    private readonly DataProvider dataProvider;
    private readonly ILogger<AffixPoolStatFilter> logger;

    private readonly object gate = new();
    private readonly Dictionary<GameType, Task<AffixPoolIndex?>> cache = [];

    public AffixPoolStatFilter(
        AffixWeightService weights,
        DataProvider dataProvider,
        ILogger<AffixPoolStatFilter> logger)
    {
        this.weights = weights;
        this.dataProvider = dataProvider;
        this.logger = logger;
    }

    /// <summary>
    /// 按游戏取索引（进程内缓存）。返回 null = 建不起来，调用方降级成「不过滤」并说明原因。
    /// </summary>
    public Task<AffixPoolIndex?> GetIndexAsync(GameType game)
    {
        lock (gate)
        {
            if (!cache.TryGetValue(game, out var task))
            {
                cache[game] = task = BuildAsync(game);
            }

            return task;
        }
    }

    private async Task<AffixPoolIndex?> BuildAsync(GameType game)
    {
        if (!weights.HasData)
        {
            logger.LogWarning("[BuildTarget] Affix weights missing, the affix search will not filter by slot");
            return null;
        }

        try
        {
            var englishStats = await dataProvider.Read<List<TradeStatDefinition>>(
                game,
                GameDataType.TradeStats,
                new GameLanguageEn());

            var index = new AffixPoolIndex(weights.Data!.Mods, englishStats);
            logger.LogInformation(
                "[BuildTarget] Affix pool index built for {Game}: {Count} stat ids",
                game,
                index.StatIdCount);

            return index;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[BuildTarget] Could not read the English trade stats for {Game}, the affix search will not filter by slot",
                game);

            return null;
        }
    }
}
