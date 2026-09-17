using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Auth;

public static partial class OfflineAuth
{
    private const string OfflinePrefix = "OfflinePlayer:";

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$")]
    private static partial Regex UsernameRegex();

    public static bool IsValidUsername(string? username)
        => !string.IsNullOrEmpty(username) && UsernameRegex().IsMatch(username);

    public static Guid ComputeUuid(string username)
    {
        if (string.IsNullOrEmpty(username))
        {
            throw new ArgumentException("Username must not be empty.", nameof(username));
        }

        var bytes = Encoding.UTF8.GetBytes(OfflinePrefix + username);
        var hash = MD5.HashData(bytes);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return Guid.ParseExact(Convert.ToHexString(hash), "N");
    }

    public static OfflineAccount Login(string username)
    {
        if (!IsValidUsername(username))
        {
            throw new ArgumentException(
                "Username must be 3-16 characters long and contain only A-Z, a-z, 0-9 and underscore.",
                nameof(username));
        }

        var uuid = ComputeUuid(username);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        return new OfflineAccount(username, uuid, token);
    }
}