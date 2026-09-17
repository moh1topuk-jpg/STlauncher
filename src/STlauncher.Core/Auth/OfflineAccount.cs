using System;

namespace STlauncher.Core.Auth;

public sealed record OfflineAccount(string Username, Guid Uuid, string AccessToken)
{
    public string UuidUndashed => Uuid.ToString("N");

    public string UserType => "legacy";
}