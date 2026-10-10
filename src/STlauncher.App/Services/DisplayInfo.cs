using System;
using System.Runtime.InteropServices;

namespace STlauncher.App.Services;

/// <summary>
/// The one thing about the monitor the launcher asks: how many frames a second it can
/// show. A frame cap above that is heat and battery for frames nobody sees.
/// </summary>
public static class DisplayInfo
{
    private const int CurrentSettings = -1;

    /// <summary>The refresh rate of the primary display, or null when the system does not say.</summary>
    public static int? RefreshRate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };

            // 0 and 1 are the driver's "whatever the hardware does", not a rate.
            return EnumDisplaySettings(null, CurrentSettings, ref mode) && mode.dmDisplayFrequency > 1
                ? mode.dmDisplayFrequency
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNumber, ref DevMode mode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
