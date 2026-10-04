using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Services;
using STlauncher.Core.Skins;

namespace STlauncher.App.ViewModels;

/// <summary>A skin from the public feed, shown until the player saves it or asks for more.</summary>
public sealed partial class RandomSkinItem : ObservableObject
{
    public RandomSkinItem(RandomSkin source)
    {
        Source = source;
    }

    public RandomSkin Source { get; }

    public PlayerSkin Skin => Source.Skin;

    /// <summary>Already in the library: the button turns into a mark, a second copy is not made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isSaved;

    public bool CanSave => !IsSaved;
}

/// <summary>
/// "Random skins": a handful of skins other people have recently uploaded to MineSkin, to
/// keep or to start a drawing from. Nothing is asked of the network until the player
/// presses the button, and nothing is kept unless they press save.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>How many skins one press shows.</summary>
    private const int RandomSkinsPerPage = 12;

    private string? _randomSkinsCursor;
    private int _randomSkinsSaved;

    public ObservableCollection<RandomSkinItem> RandomSkins { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RandomSkinsButtonText))]
    private bool _hasRandomSkins;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadRandomSkins))]
    private bool _isLoadingRandomSkins;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRandomSkinsProblem))]
    private string _randomSkinsProblem = string.Empty;

    public bool HasRandomSkinsProblem => RandomSkinsProblem.Length > 0;

    public bool CanLoadRandomSkins => !IsLoadingRandomSkins;

    public string RandomSkinsButtonText => HasRandomSkins
        ? Localize("Skins_RandomMore", "Show others")
        : Localize("Skins_RandomShow", "Show");

    [RelayCommand]
    private async Task LoadRandomSkins()
    {
        if (IsLoadingRandomSkins)
        {
            return;
        }

        IsLoadingRandomSkins = true;
        RandomSkinsProblem = string.Empty;

        try
        {
            var page = await _skins.GetRandomSkinsAsync(_randomSkinsCursor, RandomSkinsPerPage);

            if (page.Skins.Count == 0)
            {
                RandomSkinsProblem = Localize("Skins_RandomNone", "The feed did not answer, or had nothing that is a whole skin. Try again in a minute.");
                return;
            }

            _randomSkinsCursor = page.Next;
            RandomSkins.Clear();

            foreach (var skin in page.Skins)
            {
                RandomSkins.Add(new RandomSkinItem(skin));
            }

            HasRandomSkins = true;
        }
        catch (Exception ex)
        {
            AppendConsole($"[skins] random feed: {ex.Message}");
            RandomSkinsProblem = Localize("Skins_RandomNone", "The feed did not answer, or had nothing that is a whole skin. Try again in a minute.");
        }
        finally
        {
            IsLoadingRandomSkins = false;
        }
    }

    [RelayCommand]
    private void SaveRandomSkin(RandomSkinItem? item)
    {
        if (item is null || item.IsSaved)
        {
            return;
        }

        try
        {
            EnsureSkinLibraryLoaded();

            // The feed has no names; a counter that skips the ones already taken does.
            string name;

            do
            {
                name = Localize("Skins_RandomName", "Random {0}", ++_randomSkinsSaved);
            }
            while (LibrarySkins.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)));

            var entry = _skins.Library.Add(name, item.Skin.IsSlim ? SkinModel.Slim : SkinModel.Classic, item.Source.Png);

            if (ShowLibraryEntry(entry) is not null)
            {
                item.IsSaved = true;
                SaySkins(Localize("Skins_RandomSaved", "Saved to the library as «{0}». It can be renamed and edited there.", entry.Name));
            }
        }
        catch (Exception ex)
        {
            ReportUiFailure(ex);
        }
    }
}
