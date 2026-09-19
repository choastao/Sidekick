using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sidekick.Common;
using Sidekick.Common.Cache;
using Sidekick.Common.Exceptions;
using Sidekick.Common.Initialization;
using Sidekick.Common.Platform;
using Sidekick.Common.Settings;
using Sidekick.Common.Ui.Views;
using Sidekick.Modules.Initialization.Localization;
namespace Sidekick.Modules.Initialization.Components;

public partial class Initialization
{
    [Inject]
    private IViewLocator ViewLocator { get; set; } = null!;

    [Inject]
    private IStringLocalizer<InitializationResources> Resources { get; set; } = null!;

    [Inject]
    private ILogger<Initialization> Logger { get; set; } = null!;

    [Inject]
    private IApplicationService ApplicationService { get; set; } = null!;

    [Inject]
    private IServiceProvider ServiceProvider { get; set; } = null!;

    [Inject]
    private ISettingsService SettingsService { get; set; } = null!;

    [Inject]
    private CurrentView CurrentView { get; set; } = null!;

    [Inject]
    private ICacheProvider CacheProvider { get; set; } = null!;

    [Inject]
    private IOptions<SidekickConfiguration> Configuration { get; set; } = null!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    private int Count { get; set; }

    private string? Step { get; set; }

    private int Percentage { get; set; }

    public Task? InitializationTask { get; set; }

    private int Completed { get; set; }

    protected override async Task OnInitializedAsync()
    {
        InitializationTask = Handle();
        await base.OnInitializedAsync();
    }

    public async Task Handle()
    {
        try
        {
            Completed = 0;
            Count = Configuration.Value.InitializableServices.Count;

            // 兜底：没有选择赛季时（首次运行、或保存的赛季已失效），不要进入初始化流程——
            // LeagueProvider 会因为找不到联赛而抛异常，导致启动画面永久卡住。
            // 直接引导到 Setup 页让用户选择语言和赛季。
            var leagueId = await SettingsService.GetString(SettingKeys.LeagueId);
            if (string.IsNullOrEmpty(leagueId))
            {
                Logger.LogWarning("[Initialization] No league selected, redirecting to setup.");
                NavigationManager.NavigateTo("/setup");
                return;
            }

            var version = ApplicationService.GetVersion();
            var previousVersion = await SettingsService.GetString(SettingKeys.Version);
            if (version != previousVersion)
            {
                await CacheProvider.Clear();
                await SettingsService.Set(SettingKeys.Version, version);
            }

            // Report initial progress
            await ReportProgress();

            var resolver = new InitializationOrderResolver(ServiceProvider);
            var orderedServices = resolver.GetOrderedServices(Configuration.Value.InitializableServices);
            foreach (var service in orderedServices)
            {
                Logger.LogInformation($"[Initialization] Initializing {service.GetType().FullName}");
                await service.Initialize();
                Completed++;
                await ReportProgress();
            }

            // If we have a successful initialization, we delay for half a second to show the
            // "Ready" label on the UI before closing the view
            Completed = Count;
            ApplicationService.HasInitialized = true;

            await ReportProgress();
            await Task.Delay(200);
            await Complete();
        }
        catch (SidekickException e)
        {
            await SettingsService.Set(SettingKeys.LanguageParser, null);
            e.Actions = ExceptionActions.ExitApplication;
            throw;
        }
        catch (Exception e)
        {
            // 兜底：任何未预期的异常都不能再被这个未 await 的 Task 静默吞掉，
            // 否则启动画面会永远停在某一进度上，界面上没有任何提示。
            Logger.LogError(e, "[Initialization] Unexpected error while initializing.");
            await InvokeAsync(
                () =>
                {
                    Step = $"{Resources["Failed"]}: {e.Message}";
                    StateHasChanged();
                });
        }
    }

    private async Task Complete()
    {
        var redirectToHome = await SettingsService.GetBool(SettingKeys.OpenHomeOnLaunch);
        if (redirectToHome)
        {
            ViewLocator.Close(SidekickViewType.Splash);
            ViewLocator.Open(SidekickViewType.Standard, "/home");
        }
        else
        {
            CurrentView.Close();
        }
    }

    private Task ReportProgress()
    {
        return InvokeAsync(() =>
        {
            Percentage = Count == 0 ? 0 : Completed * 100 / Count;
            if (Percentage >= 100)
            {
                Step = Resources["Ready"];
                Percentage = 100;
            }
            else
            {
                Step = Resources["Title", Completed, Count];
            }

            StateHasChanged();
            return Task.Delay(100);
        });
    }

    public void Exit()
    {
        ApplicationService.Shutdown();
    }
}
