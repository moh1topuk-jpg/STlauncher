using STlauncher.Core.Friends;
using STlauncher.Core.Hosting;

namespace STlauncher.App.Services;

/// <summary>
/// Everything the "My server" page works with, handed to the view model as one piece so
/// that its constructor grows by a single parameter rather than by six.
/// </summary>
public sealed class HostingServices
{
    public HostingServices(
        HostedServerStore store,
        ServerInstaller installer,
        ServerJava java,
        ServerRunner runner,
        PlayitAgent playit,
        FriendServerStore friends)
    {
        Store = store;
        Installer = installer;
        Java = java;
        Runner = runner;
        Playit = playit;
        Friends = friends;
    }

    public HostedServerStore Store { get; }

    public ServerInstaller Installer { get; }

    public ServerJava Java { get; }

    /// <summary>One for the whole launcher: it is what knows which servers are running.</summary>
    public ServerRunner Runner { get; }

    public PlayitAgent Playit { get; }

    public FriendServerStore Friends { get; }
}
