using System;
using System.Runtime.InteropServices;

namespace STlauncher.Core.Http;

/// <summary>
/// Whether Windows considers the connection metered ("лимитное подключение"): a phone's
/// hotspot, a mobile plan, or one the player marked so. Background downloads stay off it.
/// </summary>
/// <remarks>
/// .NET 8 without the Windows SDK target has no managed call for this (the WinRT
/// NetworkInformation class needs a Windows-specific target framework). The same answer
/// comes from the Network List Manager's COM interface, which every Windows since 8 has and
/// which .NET's built-in COM support reaches without a package.
/// </remarks>
public static class MeteredConnection
{
    private const uint Fixed = 0x2;
    private const uint Variable = 0x4;
    private const uint OverDataLimit = 0x10000;
    private const uint Roaming = 0x40000;

    /// <summary>True: metered. False: not. Null: the system did not say (not Windows, or the call failed).</summary>
    public static bool? IsMetered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var manager = (INetworkCostManager)new NetworkListManager();

            try
            {
                manager.GetCost(out var cost, IntPtr.Zero);

                // 0 is "unknown": no connection Windows knows how to cost.
                return cost == 0 ? null : (cost & (Fixed | Variable | OverDataLimit | Roaming)) != 0;
            }
            finally
            {
                Marshal.ReleaseComObject(manager);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    [ComImport]
    [Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManager
    {
    }

    [ComImport]
    [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, IntPtr destinationAddress);

        void GetDataPlanStatus(IntPtr dataPlanStatus, IntPtr destinationAddress);

        void SetDestinationAddresses(uint length, IntPtr addresses, [MarshalAs(UnmanagedType.VariantBool)] bool append);
    }
}
