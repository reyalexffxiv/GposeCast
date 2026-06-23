using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GposeCast.Services;
using GposeCast.Windows;

namespace GposeCast;

/// <summary>
/// Dalamud plugin entry point.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/gposecast";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    /// <summary>Persisted configuration.</summary>
    public Configuration Configuration { get; }

    /// <summary>Object-table scanner service.</summary>
    public ActorScannerService ActorScanner { get; }

    /// <summary>Picked group service.</summary>
    public CastGroupService CastGroup { get; }

    /// <summary>Actor hide/restore service.</summary>
    public VisibilityService Visibility { get; }

    /// <summary>Overworld-to-GPose import service.</summary>
    public GposeImportService GposeImport { get; }

    /// <summary>GPose state tracker.</summary>
    public GposeStateService GposeState { get; }

    /// <summary>Dalamud window system for all plugin windows.</summary>
    public readonly WindowSystem WindowSystem = new("GposeCast");

    private ConfigWindow ConfigWindow { get; }
    private MainWindow MainWindow { get; }

    /// <summary>
    /// Creates services, windows, commands, and Dalamud callbacks.
    /// </summary>
    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.MigrateIfNeeded();
        GposeState = new GposeStateService(ClientState);
        ActorScanner = new ActorScannerService(ObjectTable, ClientState);
        CastGroup = new CastGroupService();
        Visibility = new VisibilityService(ActorScanner, Configuration);
        GposeImport = new GposeImportService(ClientState, ObjectTable, Framework, ActorScanner);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Gpose Cast while in GPose. The plugin is designed to work only inside GPose.",
        });

        // GPose hides normal Dalamud windows unless this flag is set. Brio uses the
        // same UI-builder behavior for its GPose workspace.
        PluginInterface.UiBuilder.DisableGposeUiHide = true;

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        if (Configuration.AutoOpenInGpose && GposeState.IsInGpose)
            MainWindow.IsOpen = true;

        Log.Information("Gpose Cast loaded. Player isolation/import enabled; optional NPC/pet hiding available in settings.");
    }

    /// <summary>
    /// Restores all local visibility changes and unregisters every Dalamud callback.
    /// </summary>
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.DisableGposeUiHide = false;

        Visibility.RestoreAll();
        GposeImport.Dispose();

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        MainWindow.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    /// <summary>Slash-command handler.</summary>
    private void OnCommand(string command, string args)
    {
        ToggleMainUi();
    }

    /// <summary>
    /// Handles GPose transitions. This is also the safety net that restores hidden actors
    /// when the user exits GPose.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        var transition = GposeState.UpdateAndCheckTransition();

        if (transition.Entered && Configuration.AutoOpenInGpose)
            MainWindow.IsOpen = true;

        if (!transition.Left)
            return;

        GposeImport.CancelPendingImport("GPose ended.");
        Visibility.StopIsolation();
        MainWindow.IsOpen = false;
    }

    /// <summary>Toggles the configuration window.</summary>
    public void ToggleConfigUi() => ConfigWindow.Toggle();

    /// <summary>
    /// Toggles the main window. Gpose Cast intentionally refuses to open outside
    /// GPose because its actor import and isolation features are GPose-only.
    /// </summary>
    public void ToggleMainUi()
    {
        if (!GposeState.IsInGpose)
        {
            ChatGui.PrintError("Cannot open Gpose Cast outside of GPose.");
            return;
        }

        MainWindow.Toggle();
    }
}
