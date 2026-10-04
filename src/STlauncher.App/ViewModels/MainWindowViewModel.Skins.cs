using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Services;
using STlauncher.Core.Skins;

namespace STlauncher.App.ViewModels;

/// <summary>One skin of the library, as its card and the panel beside the grid show it.</summary>
public sealed partial class SkinItem : ObservableObject
{
    public SkinItem(SkinEntry entry, PlayerSkin skin)
    {
        Id = entry.Id;
        AddedAt = entry.AddedAt;
        _name = entry.Name;
        _skin = skin;
        _isSlim = entry.SkinModel == SkinModel.Slim;
    }

    public string Id { get; }

    public DateTimeOffset AddedAt { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>The texture the card's figure wears; replaced when the editor saves.</summary>
    [ObservableProperty]
    private PlayerSkin _skin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelLabel), nameof(ModelShortLabel), nameof(FactsLabel))]
    private bool _isSlim;

    /// <summary>True for the one skin the launcher's figure has on.</summary>
    [ObservableProperty]
    private bool _isWorn;

    [ObservableProperty]
    private bool _isSelected;

    public string ModelLabel => IsSlim
        ? MainWindowViewModel.Localize("Skins_ModelSlim", "Slim arms")
        : MainWindowViewModel.Localize("Skins_ModelClassic", "Classic model");

    /// <summary>The same in a word or two, for the narrow card.</summary>
    public string ModelShortLabel => IsSlim
        ? MainWindowViewModel.Localize("Skins_ModelSlimShort", "Slim arms")
        : MainWindowViewModel.Localize("Skins_ModelClassicShort", "Classic");

    /// <summary>"Slim arms · added 4 October 2026": the line under the name.</summary>
    public string FactsLabel
        => ModelLabel + " · " + MainWindowViewModel.Localize("Skins_AddedOn", "added {0}", AddedAt.ToLocalTime().ToString("d MMMM yyyy"));
}

/// <summary>
/// The skins section: the player's own library of skins, the editor that draws them, and
/// the hand-over to the sites that show a skin to other players.
/// </summary>
/// <remarks>
/// A skin worn from here changes the figure in the launcher and nothing else. The game
/// is started offline, and what other players see is decided by the skin system their
/// server uses; the launcher cannot put a file there, only prepare it and open the right
/// page. Every text on this screen has to stay honest about that.
/// </remarks>
public partial class MainWindowViewModel
{
    /// <summary>Ely.by's "upload a skin" page; it asks to sign in when nobody is.</summary>
    private const string ElyByUploadUrl = "https://ely.by/skins/add";

    /// <summary>TLauncher's account page, where "Upload skin" lives. The Russian site is a separate address.</summary>
    private const string TLauncherProfileUrl = "https://tlauncher.org/en/profile/";

    private const string TLauncherProfileUrlRu = "https://tlauncher.ru/profile/";

    /// <summary>A skin is a few kilobytes; a file this large is something else with a .png name.</summary>
    private const long MaxSkinFileBytes = 2 * 1024 * 1024;

    private bool _skinLibraryLoaded;

    public ObservableCollection<SkinItem> LibrarySkins { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedLibrarySkin))]
    private SkinItem? _selectedLibrarySkin;

    public bool HasLibrarySkins => LibrarySkins.Count > 0;

    public bool HasSelectedLibrarySkin => SelectedLibrarySkin is not null;

    public bool IsSkinsSection => Section == ShellSection.Skins;

    public bool IsSkinSourceLibrary => SelectedSkinSource == Services.SkinSource.Library;

    /// <summary>What just happened on the page, or why a file was refused. Empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkinsNotice))]
    private string _skinsNotice = string.Empty;

    [ObservableProperty]
    private bool _isSkinsNoticeProblem;

    public bool HasSkinsNotice => SkinsNotice.Length > 0;

    /// <summary>A file is being dragged over the page.</summary>
    [ObservableProperty]
    private bool _isSkinDropHover;

    [ObservableProperty]
    private bool _isRenamingSkin;

    [ObservableProperty]
    private string _skinRenameText = string.Empty;

    [ObservableProperty]
    private bool _isConfirmingSkinDelete;

    /// <summary>What to do on the site that has just been opened. Shown after a publish button was pressed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkinPublishHint))]
    private string _skinPublishHint = string.Empty;

    public bool HasSkinPublishHint => SkinPublishHint.Length > 0;

    /// <summary>The figure's skin can be kept unless it already is a library skin.</summary>
    public bool CanSaveCurrentSkin => PlayerSkin is { } skin && skin.Source != SkinService.LibrarySource;

    /// <summary>The name of the library skin the figure wears, for the line under the skin source choice.</summary>
    private string WornSkinName => _skins.Library.Worn?.Name ?? string.Empty;

    partial void OnPlayerSkinChanged(Services.PlayerSkin? value) => OnPropertyChanged(nameof(CanSaveCurrentSkin));

    partial void OnSelectedLibrarySkinChanged(SkinItem? oldValue, SkinItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        // What was being asked about the previous skin does not carry over to this one.
        IsRenamingSkin = false;
        IsConfirmingSkinDelete = false;
        SkinPublishHint = string.Empty;
    }

    /// <summary>The "My skin" flyout's way here.</summary>
    [RelayCommand]
    private void OpenSkins() => Section = ShellSection.Skins;

    /// <summary>
    /// Reads the library the first time the page is shown. Not at startup: decoding a
    /// dozen textures is nothing, but a player who never opens the page should not pay
    /// even that.
    /// </summary>
    public void EnsureSkinLibraryLoaded()
    {
        if (_skinLibraryLoaded)
        {
            return;
        }

        _skinLibraryLoaded = true;

        try
        {
            // The list is on disk and could not be read - held by another program, say.
            // That is not an empty library: it is said so, and read again at the next look.
            if (!_skins.Library.IsRead)
            {
                _skinLibraryLoaded = false;
                SaySkins(Localize("Skins_LibraryUnreadable", "The list of your skins could not be read right now: another program may be holding the file. The skins themselves are safe. Open this page again in a moment."), problem: true);
                return;
            }

            var wornId = _skins.Library.Worn?.Id;

            foreach (var entry in _skins.Library.List())
            {
                var skin = entry.Id == wornId ? _skins.WornLibrarySkin : _skins.LoadLibrarySkin(entry);

                if (skin is not null)
                {
                    LibrarySkins.Add(new SkinItem(entry, skin));
                }
            }

            RefreshWornSkinFlags();
            OnPropertyChanged(nameof(HasLibrarySkins));
            SelectedLibrarySkin = LibrarySkins.FirstOrDefault(s => s.IsWorn) ?? LibrarySkins.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ReportUiFailure(ex);
        }
    }

    /// <summary>Marks the card of the skin the figure has on; none when the skin comes from a skin system.</summary>
    private void RefreshWornSkinFlags()
    {
        var wornId = IsSkinSourceLibrary ? _skins.Library.Worn?.Id : null;

        foreach (var item in LibrarySkins)
        {
            item.IsWorn = item.Id == wornId;
        }
    }

    private SkinItem? ShowLibraryEntry(SkinEntry entry)
    {
        EnsureSkinLibraryLoaded();

        if (LibrarySkins.FirstOrDefault(s => s.Id == entry.Id) is { } known)
        {
            SelectedLibrarySkin = known;
            return known;
        }

        if (_skins.LoadLibrarySkin(entry) is not { } skin)
        {
            return null;
        }

        var item = new SkinItem(entry, skin);
        LibrarySkins.Insert(0, item);
        OnPropertyChanged(nameof(HasLibrarySkins));
        SelectedLibrarySkin = item;
        return item;
    }

    private void SaySkins(string text, bool problem = false)
    {
        SkinsNotice = text;
        IsSkinsNoticeProblem = problem;
    }

    [RelayCommand]
    private void DismissSkinsNotice() => SkinsNotice = string.Empty;

    [RelayCommand]
    private void SelectLibrarySkin(SkinItem? item)
    {
        if (item is not null)
        {
            SelectedLibrarySkin = item;
        }
    }

    // ===================== Wearing =====================

    /// <summary>
    /// Puts the skin on the launcher's figure - home screen, rail, everywhere - by making
    /// the library the skin source. Nothing is sent anywhere.
    /// </summary>
    [RelayCommand]
    private void WearSkin(SkinItem? item)
    {
        item ??= SelectedLibrarySkin;

        if (item is null)
        {
            return;
        }

        try
        {
            // The file may have been removed behind the launcher's back; a figure
            // "wearing" nothing would be told it wears this.
            if (_skins.Library.Find(item.Id) is not { } entry || _skins.LoadLibrarySkin(entry) is null)
            {
                SaySkins(Localize("Skins_OpenFailed", "«{0}» could not be opened: the file is damaged or not a skin.", item.Name), problem: true);
                return;
            }

            _skins.Library.SetWorn(item.Id);
            _skins.ForgetWornLibrarySkin();

            if (IsSkinSourceLibrary)
            {
                _ = UpdateAvatarAsync();
            }
            else
            {
                SelectedSkinSource = Services.SkinSource.Library;
            }

            // At once, not after the pause the nickname box needs between keystrokes.
            if (_skins.WornLibrarySkin is { } worn)
            {
                PlayerSkin = worn;
            }

            RefreshWornSkinFlags();
            OnPropertyChanged(nameof(SkinSourceLabel));
            SaySkins(Localize("Skins_WornNotice", "«{0}» is now on your figure in the launcher. Other players will see it only after you publish it to the skin system your server uses.", item.Name));
        }
        catch (Exception ex)
        {
            ReportUiFailure(ex);
        }
    }

    /// <summary>Back to the skin the name has in a skin system. The library keeps the skin.</summary>
    [RelayCommand]
    private void TakeOffSkin()
    {
        if (IsSkinSourceLibrary)
        {
            SelectedSkinSource = Services.SkinSource.Auto;
            SaySkins(Localize("Skins_TakenOff", "The figure shows the skin found by your nickname again. The skin stays in the library."));
        }
    }

    // ===================== Adding =====================

    /// <summary>Copies the skin on the figure, wherever it came from, into the library.</summary>
    [RelayCommand]
    private void SaveCurrentSkin()
    {
        if (PlayerSkin is not { } skin || !CanSaveCurrentSkin)
        {
            return;
        }

        try
        {
            if (SkinPng.FromBitmap(skin.Texture) is not { } image)
            {
                SaySkins(Localize("Skins_SaveCurrentFailed", "This skin could not be read, so it was not saved."), problem: true);
                return;
            }

            var name = skin.IsDefault ? Localize("Skins_DefaultName", "Steve") : Username;
            var entry = _skins.Library.Add(name, skin.IsSlim ? SkinModel.Slim : SkinModel.Classic, SkinPng.Encode(image));

            if (ShowLibraryEntry(entry) is not null)
            {
                SaySkins(Localize("Skins_SavedCurrent", "The skin from your figure is now in the library as «{0}».", entry.Name));
            }
        }
        catch (Exception ex)
        {
            ReportUiFailure(ex);
        }
    }

    /// <summary>A fresh copy of the default skin, opened in the editor straight away.</summary>
    [RelayCommand]
    private void NewSkin(string? model)
    {
        try
        {
            var slim = string.Equals(model, "slim", StringComparison.OrdinalIgnoreCase);

            using var asset = AssetLoader.Open(new Uri($"avares://STlauncher.App/Assets/{(slim ? "alex" : "steve")}.png"));
            using var bytes = new MemoryStream();
            asset.CopyTo(bytes);

            var entry = _skins.Library.Add(
                Localize("Skins_NewName", "New skin"),
                slim ? SkinModel.Slim : SkinModel.Classic,
                bytes.ToArray());

            if (ShowLibraryEntry(entry) is { } item)
            {
                EditSkin(item);
            }
        }
        catch (Exception ex)
        {
            ReportUiFailure(ex);
        }
    }

    /// <summary>
    /// Takes PNG files from the picker or dropped on the page. Each file is copied; the
    /// original stays where it was. A file that is not a skin is refused with the reason
    /// in plain words, and the rest are still added.
    /// </summary>
    public void AddSkinFiles(IEnumerable<string> paths)
    {
        IsSkinDropHover = false;
        EnsureSkinLibraryLoaded();

        var added = 0;
        var converted = 0;
        var refused = new List<string>();
        SkinEntry? last = null;

        foreach (var path in paths)
        {
            var file = Path.GetFileName(path);

            try
            {
                var info = new FileInfo(path);

                if (!info.Exists || info.Length > MaxSkinFileBytes)
                {
                    refused.Add(Localize("Skins_RefusedNotPng", "«{0}» is not a PNG picture.", file));
                    continue;
                }

                var bytes = File.ReadAllBytes(path);
                var kind = SkinFile.Inspect(bytes, out var width, out var height);

                if (kind == SkinFileKind.NotPng)
                {
                    refused.Add(Localize("Skins_RefusedNotPng", "«{0}» is not a PNG picture.", file));
                    continue;
                }

                if (kind == SkinFileKind.WrongSize)
                {
                    refused.Add(Localize("Skins_RefusedSize", "«{0}» is {1}×{2}. A Minecraft skin is a 64×64 PNG, or 64×32 in the old format.", file, width, height));
                    continue;
                }

                if (SkinPng.Decode(bytes) is not { } image)
                {
                    refused.Add(Localize("Skins_RefusedUnreadable", "«{0}» could not be read as a picture.", file));
                    continue;
                }

                // A modern skin goes in byte for byte; an old one is rewritten in the new layout.
                var png = kind == SkinFileKind.Legacy ? SkinPng.Encode(image) : bytes;
                var name = Path.GetFileNameWithoutExtension(path);

                last = _skins.Library.Add(name, image.LooksSlim() ? SkinModel.Slim : SkinModel.Classic, png);
                ShowLibraryEntry(last);
                added++;

                if (kind == SkinFileKind.Legacy)
                {
                    converted++;
                }
            }
            catch (Exception ex)
            {
                refused.Add(Localize("Skins_RefusedError", "«{0}»: {1}", file, ex.Message));
            }
        }

        var lines = new List<string>();

        if (added > 0)
        {
            lines.Add(Localize("Skins_AddedCount", "Skins added: {0}.", added));
        }

        if (converted > 0)
        {
            lines.Add(Localize("Skins_ConvertedCount", "Converted from the old 64×32 format: {0}.", converted));
        }

        lines.AddRange(refused);

        if (lines.Count > 0)
        {
            SaySkins(string.Join(Environment.NewLine, lines), problem: refused.Count > 0);
        }
    }

    // ===================== Duplicate, rename, delete =====================

    [RelayCommand]
    private void DuplicateSkin(SkinItem? item)
    {
        item ??= SelectedLibrarySkin;

        if (item is null)
        {
            return;
        }

        try
        {
            if (_skins.Library.Duplicate(item.Id, Localize("Skins_CopyName", "{0} (copy)", item.Name)) is { } entry)
            {
                ShowLibraryEntry(entry);
            }
        }
        catch (Exception ex)
        {
            ReportUiFailure(ex);
        }
    }

    [RelayCommand]
    private void BeginRenameSkin()
    {
        if (SelectedLibrarySkin is { } item)
        {
            SkinRenameText = item.Name;
            IsConfirmingSkinDelete = false;
            IsRenamingSkin = true;
        }
    }

    [RelayCommand]
    private void CommitRenameSkin()
    {
        if (SelectedLibrarySkin is not { } item)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(SkinRenameText) &&
                _skins.Library.Rename(item.Id, SkinRenameText) is { } entry)
            {
                item.Name = entry.Name;
                OnPropertyChanged(nameof(SkinSourceLabel));
            }
        }
        catch (Exception ex)
        {
            ReportUiFailure(ex);
        }

        IsRenamingSkin = false;
    }

    [RelayCommand]
    private void CancelRenameSkin() => IsRenamingSkin = false;

    /// <summary>The first click only asks; the skin goes on the second.</summary>
    [RelayCommand]
    private void AskDeleteSkin()
    {
        IsRenamingSkin = false;
        IsConfirmingSkinDelete = SelectedLibrarySkin is not null;
    }

    [RelayCommand]
    private void CancelDeleteSkin() => IsConfirmingSkinDelete = false;

    [RelayCommand]
    private void DeleteSkin()
    {
        if (SelectedLibrarySkin is not { } item)
        {
            return;
        }

        try
        {
            var index = LibrarySkins.IndexOf(item);
            var wasWorn = item.IsWorn;

            _skins.Library.Delete(item.Id);
            _skins.ForgetWornLibrarySkin();
            LibrarySkins.Remove(item);
            OnPropertyChanged(nameof(HasLibrarySkins));

            SelectedLibrarySkin = LibrarySkins.Count == 0 ? null : LibrarySkins[Math.Min(index, LibrarySkins.Count - 1)];
            SaySkins(Localize("Skins_Deleted", "«{0}» was deleted from the library.", item.Name));

            // The figure cannot keep wearing a skin that is no longer there.
            if (wasWorn)
            {
                SelectedSkinSource = Services.SkinSource.Auto;
            }
        }
        catch (Exception ex)
        {
            ReportUiFailure(ex);
        }
    }

    // ===================== Publishing =====================

    /// <summary>
    /// Neither site takes a skin from a program, so the launcher does the part it can:
    /// puts a copy of the PNG in a known folder, copies its path, shows the file and
    /// opens the upload page. The player presses the last button there.
    /// </summary>
    [RelayCommand]
    private async Task PublishSkin(string? service)
    {
        if (SelectedLibrarySkin is not { } item)
        {
            return;
        }

        var toEly = string.Equals(service, "ElyBy", StringComparison.OrdinalIgnoreCase);

        try
        {
            var folder = _skins.Library.PublishDirectory;
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, SafeSkinFileName(item.Name) + ".png");
            File.Copy(_skins.Library.PathOf(item.Id), path, overwrite: true);

            await CopyToClipboardAsync(path);
            RevealInFileManager(path);
            OpenUrl(toEly ? ElyByUploadUrl : Language == "ru" ? TLauncherProfileUrlRu : TLauncherProfileUrl);

            SkinPublishHint = toEly
                ? Localize("Skins_PublishElyHint", "On the ely.by page that opened: sign in, choose the file (its path is in the clipboard - paste it with Ctrl+V into the file dialog), upload it and put the skin on your account.")
                : Localize("Skins_PublishTlHint", "On the TLauncher page that opened: sign in, press «Upload skin» and choose the file (its path is in the clipboard - paste it with Ctrl+V into the file dialog).");
        }
        catch (Exception ex)
        {
            SaySkins(Localize("Skins_PublishFailed", "The file could not be prepared: {0}", ex.Message), problem: true);
        }
    }

    private static string SafeSkinFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "skin" : safe;
    }

    // ===================== Editor =====================

    private string? _editingSkinId;
    private DispatcherTimer? _skinPreviewTimer;

    [ObservableProperty]
    private bool _isSkinEditorOpen;

    /// <summary>The skin on the editor's table; null while the editor is closed.</summary>
    [ObservableProperty]
    private SkinDocument? _editorDocument;

    [ObservableProperty]
    private string _editorTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkinToolPencil), nameof(IsSkinToolEraser), nameof(IsSkinToolFill), nameof(IsSkinToolPicker))]
    private SkinTool _editorTool = SkinTool.Pencil;

    public bool IsSkinToolPencil => EditorTool == SkinTool.Pencil;
    public bool IsSkinToolEraser => EditorTool == SkinTool.Eraser;
    public bool IsSkinToolFill => EditorTool == SkinTool.Fill;
    public bool IsSkinToolPicker => EditorTool == SkinTool.Picker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkinLayerBase), nameof(IsSkinLayerOverlay))]
    private SkinLayer _editorLayer = SkinLayer.Base;

    public bool IsSkinLayerBase => EditorLayer == SkinLayer.Base;
    public bool IsSkinLayerOverlay => EditorLayer == SkinLayer.Overlay;

    public bool IsEditorModelClassic => EditorDocument?.Model == SkinModel.Classic;
    public bool IsEditorModelSlim => EditorDocument?.Model == SkinModel.Slim;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorColourBrush))]
    private Color _editorColour = Color.FromRgb(0xA3, 0x24, 0x3F);

    public IBrush EditorColourBrush => new ImmutableSolidColorBrush(EditorColour);

    /// <summary>The colour as "#RRGGBB"; typing a valid one sets the colour.</summary>
    [ObservableProperty]
    private string _editorHex = "#A3243F";

    /// <summary>The colours drawn with lately, the latest first. Starts with a few every skin needs.</summary>
    public ObservableCollection<ImmutableSolidColorBrush> RecentColours { get; } = new(
        new[] { "#1E1418", "#FFFFFF", "#C68E6B", "#6B4A32", "#3A64C8", "#3F8F4C", "#E0B43C", "#A3243F" }
            .Select(hex => new ImmutableSolidColorBrush(Color.Parse(hex))));

    /// <summary>Whatever is drawn on one half of the body is drawn on the other too.</summary>
    [ObservableProperty]
    private bool _editorMirror;

    [ObservableProperty]
    private bool _editorGrid = true;

    /// <summary>The figure beside the canvas: the document's pixels, a moment behind the pencil.</summary>
    [ObservableProperty]
    private Services.PlayerSkin? _editorPreviewSkin;

    [ObservableProperty]
    private bool _isSkinUnsavedPromptOpen;

    /// <summary>
    /// Why the last save from the editor did not happen. Shown in the editor itself and in
    /// the question about unsaved changes: the page's own notice sits on the library,
    /// which the editor covers.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkinEditorProblem))]
    private string _skinEditorProblem = string.Empty;

    public bool HasSkinEditorProblem => SkinEditorProblem.Length > 0;

    public bool CanUndoSkin => EditorDocument?.CanUndo == true;

    public bool CanRedoSkin => EditorDocument?.CanRedo == true;

    public bool IsSkinEditorDirty => EditorDocument?.IsDirty == true;

    public string EditorStateLabel => IsSkinEditorDirty
        ? Localize("Skins_StateUnsaved", "There are unsaved changes")
        : Localize("Skins_StateSaved", "Everything is saved");

    partial void OnEditorColourChanged(Color value)
    {
        // Left alone while it already says this colour: rewriting "#fa0" into "#FFAA00"
        // under the player's fingers moves the caret.
        if (!SkinColour.TryParseHex(EditorHex, out var typed) || typed != value.ToUInt32())
        {
            EditorHex = SkinColour.ToHex(value.ToUInt32());
        }
    }

    partial void OnEditorHexChanged(string value)
    {
        if (SkinColour.TryParseHex(value, out var colour) && colour != EditorColour.ToUInt32())
        {
            EditorColour = Color.FromUInt32(colour);
        }
    }

    partial void OnEditorMirrorChanged(bool value)
    {
        if (EditorDocument is { } document)
        {
            document.Mirror = value;
        }
    }

    [RelayCommand]
    private void SelectSkinTool(string? tool)
    {
        if (Enum.TryParse<SkinTool>(tool, ignoreCase: true, out var parsed))
        {
            EditorTool = parsed;
        }
    }

    [RelayCommand]
    private void SelectSkinLayer(string? layer)
    {
        if (Enum.TryParse<SkinLayer>(layer, ignoreCase: true, out var parsed))
        {
            EditorLayer = parsed;
        }
    }

    [RelayCommand]
    private void SelectSkinModel(string? model)
    {
        if (EditorDocument is { } document && Enum.TryParse<SkinModel>(model, ignoreCase: true, out var parsed))
        {
            document.Model = parsed;
        }
    }

    [RelayCommand]
    private void PickRecentColour(ImmutableSolidColorBrush? brush)
    {
        if (brush is not null)
        {
            EditorColour = brush.Color;

            // A colour is chosen to draw with; with the eraser or the pipette in hand
            // the next click would do something else.
            if (EditorTool is SkinTool.Eraser or SkinTool.Picker)
            {
                EditorTool = SkinTool.Pencil;
            }
        }
    }

    /// <summary>The pipette has taken a colour: the pencil comes back, as it does in every paint program.</summary>
    public void SkinColourPicked()
    {
        if (EditorTool == SkinTool.Picker)
        {
            EditorTool = SkinTool.Pencil;
        }
    }

    [RelayCommand]
    private void EditSkin(SkinItem? item)
    {
        item ??= SelectedLibrarySkin;

        if (item is null)
        {
            return;
        }

        try
        {
            if (SkinPng.Decode(File.ReadAllBytes(_skins.Library.PathOf(item.Id))) is not { } image)
            {
                SaySkins(Localize("Skins_OpenFailed", "«{0}» could not be opened: the file is damaged or not a skin.", item.Name), problem: true);
                return;
            }

            CloseEditorDocument();

            var document = new SkinDocument(image, item.IsSlim ? SkinModel.Slim : SkinModel.Classic) { Mirror = EditorMirror };
            document.Changed += OnEditorDocumentChanged;

            _editingSkinId = item.Id;
            SelectedLibrarySkin = item;
            EditorTitle = item.Name;
            EditorLayer = SkinLayer.Base;
            EditorTool = SkinTool.Pencil;
            EditorDocument = document;
            RefreshSkinPreview();
            RaiseEditorState();
            SkinsNotice = string.Empty;
            IsSkinEditorOpen = true;
        }
        catch (Exception ex)
        {
            ReportUiFailure(ex);
        }
    }

    private void OnEditorDocumentChanged()
    {
        RaiseEditorState();

        if (EditorTool is SkinTool.Pencil or SkinTool.Fill)
        {
            RememberColour(EditorColour);
        }

        // The figure follows the pencil a moment behind: a stroke reports every pixel,
        // and rebuilding the figure's texture for each of them would be work nobody sees.
        _skinPreviewTimer ??= CreateSkinPreviewTimer();
        _skinPreviewTimer.Start();
    }

    private DispatcherTimer CreateSkinPreviewTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) => RefreshSkinPreview();
        return timer;
    }

    private void RefreshSkinPreview()
    {
        _skinPreviewTimer?.Stop();

        var previous = EditorPreviewSkin;

        EditorPreviewSkin = EditorDocument is { } document
            ? new Services.PlayerSkin(SkinPng.ToBitmap(document.Image), isDefault: false, document.Model == SkinModel.Slim)
            : null;

        // The figure has let go of the old texture by now; nothing else ever held it.
        previous?.Release();
    }

    private void RaiseEditorState()
    {
        OnPropertyChanged(nameof(CanUndoSkin));
        OnPropertyChanged(nameof(CanRedoSkin));
        OnPropertyChanged(nameof(IsSkinEditorDirty));
        OnPropertyChanged(nameof(EditorStateLabel));
        OnPropertyChanged(nameof(IsEditorModelClassic));
        OnPropertyChanged(nameof(IsEditorModelSlim));
    }

    private void RememberColour(Color colour)
    {
        if (RecentColours.Count > 0 && RecentColours[0].Color == colour)
        {
            return;
        }

        for (var i = RecentColours.Count - 1; i >= 0; i--)
        {
            if (RecentColours[i].Color == colour)
            {
                RecentColours.RemoveAt(i);
            }
        }

        RecentColours.Insert(0, new ImmutableSolidColorBrush(colour));

        while (RecentColours.Count > 12)
        {
            RecentColours.RemoveAt(RecentColours.Count - 1);
        }
    }

    [RelayCommand]
    private void UndoSkin() => EditorDocument?.Undo();

    [RelayCommand]
    private void RedoSkin() => EditorDocument?.Redo();

    /// <summary>Writes the drawing to the library. The figure in the launcher changes too if it wears this skin.</summary>
    [RelayCommand]
    private void SaveSkin() => TrySaveSkin();

    private bool TrySaveSkin()
    {
        if (EditorDocument is not { } document || _editingSkinId is not { } id)
        {
            return false;
        }

        SkinEditorProblem = string.Empty;

        try
        {
            document.EndStroke();

            if (!_skins.Library.Update(id, SkinPng.Encode(document.Image), document.Model))
            {
                SkinEditorProblem = Localize("Skins_SaveGone", "This skin is no longer in the library, so there is nowhere to save it.");
                return false;
            }

            document.MarkSaved();
            RaiseEditorState();

            var worn = _skins.Library.Worn?.Id == id;

            if (worn)
            {
                _skins.ForgetWornLibrarySkin();
            }

            if (_skins.Library.Find(id) is { } entry &&
                (worn ? _skins.WornLibrarySkin : _skins.LoadLibrarySkin(entry)) is { } skin &&
                LibrarySkins.FirstOrDefault(s => s.Id == id) is { } item)
            {
                item.Skin = skin;
                item.IsSlim = entry.SkinModel == SkinModel.Slim;

                if (worn && IsSkinSourceLibrary)
                {
                    PlayerSkin = skin;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            // Said where the player is. The library's notice is behind the editor, and a
            // save that fails without a word looks like a button that does nothing.
            SkinEditorProblem = Localize("Skins_SaveFailed", "The skin could not be saved: {0}", ex.Message);
            AppendConsole($"[skins] save: {ex}");
            return false;
        }
    }

    /// <summary>Back to the library; with unsaved changes it asks first.</summary>
    [RelayCommand]
    private void CloseSkinEditor()
    {
        EditorDocument?.EndStroke();

        if (IsSkinEditorDirty)
        {
            IsSkinUnsavedPromptOpen = true;
            return;
        }

        CloseEditorDocument();
    }

    /// <summary>
    /// Asked by whatever is about to end the launcher - the window being closed, "Quit" in
    /// the tray, the restart into an update. True means "not yet": there is a drawing that
    /// was never saved, so the editor comes to the front with its question on the screen.
    /// </summary>
    public bool HoldCloseForSkinEditor()
    {
        if (!IsSkinEditorOpen || !IsSkinEditorDirty)
        {
            return false;
        }

        Section = ShellSection.Skins;
        CloseSkinEditor();
        return true;
    }

    [RelayCommand]
    private void SaveAndCloseSkinEditor()
    {
        // A save that failed leaves the question where it is, with the reason in it: the
        // only other way out of the editor throws the drawing away.
        if (TrySaveSkin())
        {
            CloseEditorDocument();
        }
    }

    [RelayCommand]
    private void DiscardSkinChanges() => CloseEditorDocument();

    [RelayCommand]
    private void StayInSkinEditor() => IsSkinUnsavedPromptOpen = false;

    private void CloseEditorDocument()
    {
        IsSkinUnsavedPromptOpen = false;
        IsSkinEditorOpen = false;
        SkinEditorProblem = string.Empty;
        _skinPreviewTimer?.Stop();

        if (EditorDocument is { } document)
        {
            document.Changed -= OnEditorDocumentChanged;
        }

        EditorDocument = null;
        _editingSkinId = null;

        var preview = EditorPreviewSkin;
        EditorPreviewSkin = null;
        preview?.Release();
        RaiseEditorState();
    }
}
