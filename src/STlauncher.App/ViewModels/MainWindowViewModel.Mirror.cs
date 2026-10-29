using Avalonia.Threading;
using STlauncher.Core.Http;

namespace STlauncher.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// Tells the player once, in the console and the status line, that Modrinth or
    /// CurseForge files now come the second way (the owner's server) because the direct
    /// route failed. The fallback raises it once per source and session.
    /// </summary>
    public void WatchCdnFallback(CdnFallback fallback)
    {
        fallback.FallbackStarted += source =>
        {
            var text = Localize("Status_CdnFallback", "{0} is not reachable directly; files come through the backup route", source);
            AppendConsole("[mirror] " + text);
            Dispatcher.UIThread.Post(() => Status = text);
        };
    }
}
