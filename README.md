# STlauncher

Открытый лаунчер Minecraft для Windows: вход по нику, любые версии и загрузчики модов,
готовая сборка сервера [Showtime](https://showtime.su) из коробки.

> Не связан с Mojang Studios и Microsoft. Minecraft — торговая марка Mojang Studios.

## Скачать

**[Установщик для Windows](https://showtime-updates.moh1topuk.workers.dev/STlauncher-win-Setup.exe)** —
работает, даже если GitHub у вас не открывается. Установщик один на несколько версий:
после установки лаунчер сам обновится до последней.
Все версии и портативная сборка — на [странице релизов](https://github.com/moh1topuk-jpg/STlauncher/releases).

Ничего ставить отдельно не нужно: Java лаунчер скачает сам. Дальше он обновляется сам и
сообщает о новой версии. Что поменялось — в [списке изменений](CHANGELOG.md).

**Windows пишет «Система Windows защитила ваш компьютер».** Установщик пока не подписан,
поэтому SmartScreen его не знает. Нажмите «Подробнее» → «Выполнить в любом случае».
Проверить, что файл настоящий, можно так — см. [ниже](#проверка-подлинности).

**Подпись кода.** Проект использует [SignPath Foundation](https://signpath.org) — бесплатную
подпись кода для open-source проектов; заявка подана, после её одобрения релизы будут
подписаны и предупреждение исчезнет. Подробнее — [docs/signing.md](docs/signing.md).
*Code signing for this project is provided by [SignPath Foundation](https://signpath.org).*

## Возможности

- **Играть сразу.** При первом запуске лаунчер сам создаёт рекомендуемую сборку и скачивает
  её моды. Нажали «Играть» — играете.
- **Вход по нику**, без аккаунта. Ник придумывается при первом запуске, его можно сменить.
- **Любые версии** — релизы, снапшоты, старые альфы и беты.
- **Загрузчики** Fabric, Quilt, Forge и NeoForge.
- **Сборки.** У каждой сборки свои версия, моды, миры и настройки.
- **Каталог модов Modrinth** прямо в лаунчере: поиск, категории, описания на русском.
  Версия мода подбирается под сборку.
- **Моды сборки.** Любой мод можно выключить или удалить, добавить свои — положить `.jar`
  в папку модов. Моды рекомендуемой сборки только сверяются при запуске, докачиваются
  лишь недостающие.
- **Переход из других лаунчеров.** Лаунчер находит готовые сборки TLauncher, официального
  лаунчера, Prism, PolyMC, MultiMC, CurseForge, Modrinth App, GDLauncher, ATLauncher,
  FTB App, Technic и XMCL — или в любой указанной папке. Сборку можно подключить на месте
  или скопировать к себе.
- **Модпаки** `.mrpack` с проверкой файлов.
- **Бэкапы миров** — раз в сутки, перед изменением модов или перед каждым запуском.
- **Сервер.** Онлайн за сутки и неделю, рекорд, аптайм, место в рейтинге Top-Minecrafter,
  вход на сервер одной кнопкой.

## Где лежат данные

Всё хранится в `%APPDATA%\STlauncher`: сборки (`instances`), версии игры, Java, логи и
настройки. Удаление лаунчера через «Установку и удаление программ» эту папку не трогает —
сборки и миры переживут переустановку.

Логи игры — в `%APPDATA%\STlauncher\logs`. Если игра закрылась с ошибкой, лаунчер
покажет кнопку «Открыть лог».

## Проверка подлинности

Каждый релиз собирается на GitHub Actions из этого репозитория и содержит:

- `SHA256SUMS.txt` — контрольные суммы файлов:

  ```powershell
  Get-FileHash .\STlauncher-win-Setup.exe -Algorithm SHA256
  ```

- **attestation** — подтверждение GitHub, что файл собран этим репозиторием:

  ```powershell
  gh attestation verify STlauncher-win-Setup.exe -R moh1topuk-jpg/STlauncher
  ```

## Для разработчиков

Нужны Windows 10/11 и [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build
dotnet test
dotnet run --project src/STlauncher.App
```

Как вносить изменения, писать список изменений и выпускать версии — в
[CONTRIBUTING.md](CONTRIBUTING.md).

### Устройство

```
src/STlauncher.Core/     вся логика, без интерфейса
  Metadata/              версии Mojang, профили, правила, наследование профилей
  Launch/                библиотеки, natives, сборка команды запуска, процесс игры
  Loaders/               Fabric, Quilt, Forge, NeoForge
  Java/                  поиск и загрузка Java
  Http/                  параллельные загрузки, хэши, повторы, разбор сетевых ошибок
  Instances/             сборки и servers.dat
  Content/               каталог сервера и синхронизация рекомендуемой сборки
  Import/                поиск и импорт сборок других лаунчеров
  Mods/, Modpacks/       моды, Modrinth, импорт .mrpack
  Server/                пинг сервера и история онлайна
  Backups/, Nbt/, Auth/  бэкапы, NBT, офлайн-вход
src/STlauncher.App/      интерфейс на Avalonia (MVVM), обновления через Velopack
tests/                   тесты ядра (xUnit)
tools/STlauncher.Cli/    консольная утилита для отладки без интерфейса
workers/                 Cloudflare Workers: статистика онлайна и зеркало обновлений
scripts/make-app-icon.ps1  иконка .exe из логотипа (Assets/server-logo.png)
catalog.json             каталог сервера: рекомендуемая сборка, моды, адреса сервисов
```

### Документация

- [docs/catalog.md](docs/catalog.md) — `catalog.json`: рекомендуемая сборка и моды.
  Меняется без выпуска новой версии лаунчера.
- [docs/updates.md](docs/updates.md) — выпуск версий и зеркало обновлений.
- [docs/monitoring.md](docs/monitoring.md) — сборщик статистики онлайна.
- [docs/privacy.md](docs/privacy.md) — политика конфиденциальности: что лаунчер отправляет и как это выключить.
- [docs/signing.md](docs/signing.md) — подпись установщика и SmartScreen.
- [docs/platforms.md](docs/platforms.md) — Linux и macOS: что готово, что не проверено.

### Консольная утилита

Проверяет весь путь запуска без интерфейса:

```powershell
dotnet run --project tools/STlauncher.Cli -- versions
dotnet run --project tools/STlauncher.Cli -- prepare 1.21.11 Steve   # скачать и собрать команду
dotnet run --project tools/STlauncher.Cli -- run 1.21.11 Steve --loader fabric
dotnet run --project tools/STlauncher.Cli -- build-plan --catalog catalog.json   # что поставит сборка
```

Остальные команды: `java`, `loaders`, `install-loader`, `plan`, `modpack`, `catalog`,
`catalog-install`, `mods-search`, `build-install`, `server-status`.

## Безопасность

Об уязвимостях — приватно, см. [SECURITY.md](SECURITY.md).

## Лицензия

MIT — см. [LICENSE](LICENSE).
