using System.Collections.Generic;

namespace STlauncher.App.Controls;

/// <summary>
/// One setting as the search for everything knows it: the language key of its title, the
/// key of the section it sits in, and the words a player might type for it.
/// </summary>
/// <param name="Developer">Shown on the page only while the developer console is switched on.</param>
public sealed record SettingsSearchEntry(string TitleKey, string SectionKey, string Keywords, bool Developer = false);

/// <summary>
/// The settings, listed for the search in the top strip. The settings page keeps its own
/// search (<see cref="SettingsSearch"/>), which reads the page itself; this table exists
/// so the settings can be found from anywhere without walking a page that may not have
/// been shown yet. A result opens the page with the setting's title typed into the page's
/// own search, so the two only have to agree on titles.
/// <para>
/// A new setting gets one line here with the same keywords as its row on the page. A test
/// checks that every key is in both language files.
/// </para>
/// </summary>
public static class SettingsSearchIndex
{
    public static IReadOnlyList<SettingsSearchEntry> Entries { get; } = new SettingsSearchEntry[]
    {
        // Game
        new("Perf_Title", "Settings_TabGame", "производительность графика фпс fps лаги тормозит оптимизация качество performance graphics preset lag"),
        new("Settings_MemoryTitle", "Settings_TabGame", "память ram озу оперативка оперативная гб мб memory gb mb xmx heap"),
        new("Settings_ResolutionTitle", "Settings_TabGame", "разрешение окно размер экран ширина высота полный resolution window size width height fullscreen"),

        // Look
        new("Settings_Theme", "Settings_SectionLook", "тема темная светлая ночная дневная theme dark light night"),
        new("Settings_Accent", "Settings_SectionLook", "цвет акцент оформление красный синий color colour accent"),
        new("Settings_Language", "Settings_SectionLook", "язык русский английский перевод language russian english locale"),
        new("Settings_UiScale", "Settings_SectionLook", "масштаб размер интерфейса шрифт мелко крупно 4k scale zoom font size dpi"),
        new("Settings_AnimatedBackground", "Settings_SectionLook", "фон анимация искры живой background animation embers"),

        // Launcher
        new("Settings_AfterLaunch", "Settings_SectionLauncher", "после запуска закрывать сворачивать скрывать трей after launch close hide minimize tray"),
        new("Settings_DiscordPresence", "Settings_SectionLauncher", "дискорд статус активность discord presence status rpc"),
        new("Settings_UsageStats", "Settings_SectionLauncher", "статистика телеметрия анонимно приватность данные statistics telemetry privacy analytics"),
        new("Settings_BackupsTitle", "Settings_SectionLauncher", "бэкап бекап резервная копия копии сохранения миры восстановить откат backup restore saves worlds"),

        // More
        new("Settings_DataFolder", "Settings_SectionMore", "папка данных диск место перенести переместить путь хранение data folder directory disk drive move location storage"),
        new("Settings_SharedFiles", "Settings_SectionMore", "общие файлы место диск дубликаты очистка освободить shared files disk space duplicates cleanup dedupe"),
        new("Settings_GameDirectory", "Settings_SectionMore", "папка игры сборки minecraft каталог открыть game folder directory instance open"),
        new("Settings_Versions", "Settings_SectionMore", "версии снапшоты снапшот старые альфа бета versions snapshots old alpha beta"),
        new("Settings_ExtraArgs", "Settings_SectionMore", "аргументы параметры запуска ключи arguments args launch options flags"),
        new("Settings_NetworkCheck", "Settings_SectionMore", "сеть интернет соединение прокси впн не скачивается загрузка network internet connection proxy vpn download"),
        new("Report_Title", "Settings_SectionMore", "отчет поддержка помощь логи журнал админ ошибка report support help logs admin bug"),
        new("Settings_DiscreteGpu", "Settings_SectionMore", "видеокарта дискретная графика ноутбук gpu nvidia amd radeon geforce graphics card laptop"),
        new("Settings_ForceUpdate", "Settings_SectionMore", "перекачать переустановить файлы игры починить проверить redownload reinstall repair verify files"),
        new("Settings_DevConsole", "Settings_SectionMore", "консоль разработчика лог журнал java джава обновления console developer log debug updates"),

        // For developers
        new("Settings_TabJava", "Settings_Advanced", "java джава ява jdk jre jvm", Developer: true),
        new("Settings_Updates", "Settings_Advanced", "обновление обновить версия лаунчера update upgrade version release", Developer: true)
    };
}
