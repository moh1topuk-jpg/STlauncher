# Linux и macOS

Лаунчер выпускается только для Windows. Код при этом почти не привязан к Windows, и
эта страница держит порт в рабочем состоянии, пока он не понадобится.

## Что уже кроссплатформенно

- Avalonia, Velopack и Discord RPC работают на Windows, Linux и macOS.
- Java качается с Adoptium под текущую ОС и архитектуру; zip на Windows, tar.gz на
  Linux и macOS распаковываются с сохранением прав на запуск (`JavaManager.ExtractArchive`).
- Установленная Java ищется по путям каждой системы: `/usr/lib/jvm`, `~/.jdks`, SDKMAN,
  `/Library/Java/JavaVirtualMachines`, Homebrew (`JavaManager.KnownVendorRoots`).
- Правила из version.json (нативные библиотеки, аргументы JVM для macOS) разбираются по
  имени ОС и архитектуре (`RuleContext`); разделитель classpath берётся из системы.
- Сборки других лаунчеров ищутся там, где они лежат на каждой системе: `~/.local/share`
  на Linux, `~/Library/Application Support` на macOS (`ExternalInstanceScanner.DefaultRoots`).
- Папка данных — из системных путей, на Linux это `~/.config/STlauncher`.

## Сборка Linux в CI

Задача `build-linux` в [ci.yml](../.github/workflows/ci.yml) на каждом пуше прогоняет
тесты на Ubuntu, публикует `linux-x64` и упаковывает AppImage через Velopack. Результат
лежит в артефактах запуска (`STlauncher-linux-x64`, 30 дней), в релиз не попадает.
Задача помечена `continue-on-error`: её падение не блокирует релиз для Windows, но видно
в списке запусков.

Чтобы дать сборку тестировщику: открыть запуск CI на GitHub → Artifacts →
`STlauncher-linux-x64`. Внутри `STlauncher.AppImage`; сделать исполняемым и запустить.

## Что не проверено

Никто не запускал лаунчер на Linux или macOS вживую. Ожидаемые места, где нужна проверка:

- шапка окна без системной рамки (`ExtendClientAreaToDecorationsHint`) под X11 и Wayland;
- иконка в трее: на Linux нужен StatusNotifier (KDE, GNOME с расширением);
- открытие папок и ссылок через `xdg-open`;
- обновления: фид для Linux называется `releases.linux.json`, зеркало отдаёт любой файл
  релиза по имени, менять его не нужно — но проверить стоит.

## macOS

Технически то же, что Linux, плюс две сборки (Intel и Apple Silicon) и бандл `.app`.
Главное препятствие не код: без подписи и нотаризации macOS 15 не открывает скачанное
приложение, обход только через настройки безопасности. Для подписи нужен Apple Developer
Program (99 $ в год). Пока нет способа его оплатить и игрока с Mac для проверки, порт
на macOS не начинается.
