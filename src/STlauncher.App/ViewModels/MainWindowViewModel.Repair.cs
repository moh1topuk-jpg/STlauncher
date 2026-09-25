using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Content;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// "It worked yesterday" has three usual causes: a mod file that did not come down whole,
/// a mod the catalog lists that went missing, and two versions of one mod side by side.
/// One button goes through all three. Nothing is deleted: older mod files are switched
/// off, and the game's own files are checked again on the next launch.
/// </summary>
public partial class MainWindowViewModel
{
    public bool CanRepairBuild => SelectedInstance is not null && !IsBusy && !IsGameRunning && !IsBuildSyncBusy;

    [RelayCommand]
    private async Task RepairBuildAsync()
    {
        if (SelectedInstance is not { } instance || IsBusy || IsGameRunning || IsBuildSyncBusy)
        {
            return;
        }

        var reinstalled = 0;
        var fixedCount = 0;

        try
        {
            IsBuildSyncBusy = true;
            Status = Localize("Repair_Running", "Repairing the build…");
            AppendConsole($"--- Repair: {instance.Name} ---");

            // 1. Forget every verification. Each file is hashed again the next time it is
            //    needed: the catalog mods right now, the game's files at the next launch.
            _verifiedFiles.Clear();
            AppendConsole("[repair] verified-file cache cleared; every file will be hashed again");

            // 2. Every catalog mod of the build, whether the record says it is there or not.
            if (IsCatalogInstance)
            {
                var plan = PlanBuildSync(instance);
                var all = instance.EnabledCatalogItems
                    .Select(id => _loadedCatalog?.FindItem(id))
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .ToList();

                if (all.Count == 0 && plan.Pending.Count == 0)
                {
                    AppendConsole("[repair] the catalog is not loaded; the mods could not be checked against it");
                }
                else
                {
                    var everything = plan with { Pending = all.Count > 0 ? all : plan.Pending };
                    reinstalled = await ApplyBuildSyncPlanAsync(instance, everything);
                    AppendConsole($"[repair] {all.Count} catalog mod(s) checked, {reinstalled} downloaded again");
                }
            }

            RefreshMods();

            // 3. Doubles, wrong loader, wrong game version: the switch fixes, not the catalog trips.
            var directory = _instances.GameDirectory(instance);
            var issues = await Task.Run(() => BuildChecker.Check(directory, instance.Loader, instance.VersionId));
            var fixable = issues.Where(i => i.Kind != BuildIssueKind.MissingDependency || i.DisabledFileName is not null).ToList();
            fixedCount = ApplySwitchFixes(fixable);

            Status = IsCatalogInstance
                ? Localize("Repair_Done", "Build repaired: {0} mod(s) downloaded again, {1} problem(s) fixed. The game files are checked on the next launch.", reinstalled, fixedCount)
                : Localize("Repair_DoneOwn", "Build repaired: {0} problem(s) fixed. The game files are checked on the next launch.", fixedCount);
            AppendConsole($"[repair] done: {reinstalled} downloaded again, {fixedCount} fixed");
        }
        catch (Exception ex)
        {
            Status = Localize("Repair_Failed", "Could not repair the build: {0}", ex.Message);
            AppendConsole($"[repair] failed: {ex}");
            _ = ExplainDownloadFailureAsync(instance.Name, ex);
        }
        finally
        {
            IsBuildSyncBusy = false;
            RefreshMods();
            ScheduleBuildCheck();
        }
    }
}
