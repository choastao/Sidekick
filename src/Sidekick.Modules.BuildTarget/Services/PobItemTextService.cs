using Microsoft.Extensions.Logging;
using Sidekick.Common.Settings;
using Sidekick.Common.Settings.Languages;
using Sidekick.Game;
using Sidekick.Game.Parser.Items;
using Sidekick.Game.Providers;
using Sidekick.Game.TradeStats;
using Sidekick.Modules.BuildTarget.Models;

namespace Sidekick.Modules.BuildTarget.Services;

/// <summary>
/// 加载「英文（invariant）stat 模板表」并调用 <see cref="PobItemText"/> 做转换。
///
/// 模板表就是英文的 <c>trade-stats.json</c>（Sidekick 为了「查价走英文站」本来就加载的那份，
/// 我们用同一个 DataProvider + InvariantLanguage 读，**不自己解析文件路径**）。
/// 按游戏类型缓存一次，之后纯内存查表。
/// </summary>
public class PobItemTextService(
    DataProvider dataProvider,
    ISettingsService settingsService,
    ICurrentGameLanguage currentGameLanguage,
    ILogger<PobItemTextService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    private Dictionary<string, string>? templates;
    private GameType loadedGame;

    /// <summary>
    /// 把物品转成 PoB 认得的英文文本。取不到英文模板表时返回 null（调用方按「引擎算不了」处理），
    /// **不要**用空表兜底 —— 空表会让每条词缀都变成"引擎不认识"，结论偏乐观。
    /// </summary>
    public async Task<PobItemText.BuildResult?> BuildAsync(Item? item)
    {
        if (item == null)
        {
            return null;
        }

        var table = await GetTemplatesAsync(item.Game);
        if (table == null)
        {
            return null;
        }

        try
        {
            return PobItemText.Build(item, table);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BuildTarget] Failed to build PoB item text");
            return null;
        }
    }

    private async Task<Dictionary<string, string>?> GetTemplatesAsync(GameType game)
    {
        if (templates != null && loadedGame == game)
        {
            return templates;
        }

        await gate.WaitAsync();
        try
        {
            if (templates != null && loadedGame == game)
            {
                return templates;
            }

            var definitions = await dataProvider.Read<List<TradeStatDefinition>>(
                game,
                GameDataType.TradeStats,
                currentGameLanguage.InvariantLanguage);

            if (definitions == null)
            {
                logger.LogWarning("[BuildTarget] Invariant trade-stat table is unavailable");
                return null;
            }

            var table = new Dictionary<string, string>(definitions.Count, StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                // 同一个 id 可能有多条（不同 option），第一条够用：我们只借它的文本模板。
                table.TryAdd(definition.Id, definition.Text);
            }

            templates = table;
            loadedGame = game;
            logger.LogInformation("[BuildTarget] Loaded {Count} invariant stat templates for PoB conversion", table.Count);
            return table;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BuildTarget] Failed to load invariant stat templates");
            return null;
        }
        finally
        {
            gate.Release();
        }
    }
}
