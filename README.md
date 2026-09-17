# STlauncher

Открытый лаунчер Minecraft с офлайн-режимом (вход по нику), поддержкой всех версий,
мод-лоадеров и интеграцией собственного сервера через пресет.

> Не связан с Mojang Studios и Microsoft. Minecraft — торговая марка Mojang Studios.

## Возможности

- **Все версии** — релизы, снапшоты и старые альфы/беты из `version_manifest` Mojang.
- **Офлайн-вход** — UUID v3 генерируется из ника по алгоритму `OfflinePlayer:<ник>`, полностью совместим с ванильным.
- **Авто-загрузка Java** — поиск установленной JRE, при отсутствии — автоматическое скачивание Adoptium JRE нужной мажорной версии.
- **Мод-лоадеры** — Fabric, Quilt (через profile JSON), Forge, NeoForge (через installer).
- **Инстансы** — несколько независимых профилей: своя версия, лоадер, память, моды и свой `servers.dat` у каждого.
- **Моды** — поиск и установка с Modrinth, вкл/выкл и удаление в `mods/`.
- **Модпаки** — импорт `.mrpack` (Modrinth) и `.zip` (CurseForge) с проверкой хэшей и распаковкой overrides.
- **Свой сервер** — пресет в отдельной вкладке, авто-добавление сервера в `servers.dat` для любой версии, быстрый вход через `--quickPlayMultiplayer`.
- **Скины** — отображение головы по нику.
- **Авто-обновление** — через Velopack (дельта-обновления, самоподмена).

## Требования

### Для игрока
Ничего. Приложение поставляется одним self-contained `.exe`, Java скачивается автоматически.

### Для разработки
- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Сборка и запуск

```powershell
dotnet build STlauncher.sln
dotnet test STlauncher.sln
dotnet run --project src/STlauncher.App
```

Публикация одним файлом:

```powershell
dotnet publish src/STlauncher.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Структура

```
src/
  STlauncher.Core/        логика без UI (переиспользуемая)
    Metadata/             version_manifest, version JSON, rules, слияние inheritsFrom
    Http/                 параллельные загрузки, SHA1/SHA256, ретраи, кэш
    Assets/               asset index, objects, virtual/legacy
    Java/                 поиск и авто-скачивание JRE
    Auth/                 офлайн-авторизация (UUID v3)
    Launch/               резолв библиотек, natives, сборка команды, запуск процесса
    Loaders/              Fabric, Quilt, Forge, NeoForge
    Mods/                 менеджер mods/ и Modrinth API
    Modpacks/             импорт .mrpack (Modrinth)
    Instances/            инстансы (профили) и servers.dat (NBT)
    Nbt/                  минимальный NBT reader/writer
  STlauncher.App/         Avalonia UI (MVVM)
tools/
  STlauncher.Cli/         CLI-харнесс для отладки и сквозных проверок
tests/
  STlauncher.Core.Tests/  xUnit
```

## CLI

Отдельный консольный проект — удобен для отладки и сквозных проверок без GUI:

```powershell
dotnet run --project tools/STlauncher.Cli -- versions --all
dotnet run --project tools/STlauncher.Cli -- java
dotnet run --project tools/STlauncher.Cli -- loaders fabric 1.20.1
dotnet run --project tools/STlauncher.Cli -- install-loader fabric 1.20.1
dotnet run --project tools/STlauncher.Cli -- plan 1.20.1
dotnet run --project tools/STlauncher.Cli -- prepare 1.20.1 Steve
dotnet run --project tools/STlauncher.Cli -- run 1.20.1 Steve --loader fabric --server play.example.com
dotnet run --project tools/STlauncher.Cli -- modpack pack.mrpack
```

`prepare` скачивает всё, собирает команду и печатает её (без запуска игры) — так
проверяется весь путь, кроме самого запуска. `run` дополнительно запускает игру.

## Данные

Всё хранится в `%APPDATA%\STlauncher`: версии, библиотеки, assets, рантаймы Java,
инстансы и `settings.json`.

## Инстансы

Каждый инстанс — отдельный профиль со своей папкой в `instances/`: версия игры,
лоадер, память, разрешение, набор модов и собственный `servers.dat`.
Версия, лоадер и память хранятся в `instances/<id>/instance.json`, поэтому переключение
инстанса полностью меняет окружение запуска. Старые настройки из `settings.json`
автоматически переносятся в инстанс «Default» при первом запуске новой версии.

## Пресет сервера

Имя и адрес сервера задаются во вкладке **Server** и по умолчанию предзаполнены
(`mc.showtime.su`). Адрес автоматически добавляется в `servers.dat` инстанса при
любом запуске, поэтому сервер всегда виден во внутриигровом списке серверов.
Автовход происходит **только** по кнопке «Play on server» (`--quickPlayMultiplayer`),
обычный Play просто запускает игру.

## CurseForge API-ключ

Импорт CurseForge-модпаков требует API-ключ ([получить здесь](https://console.curseforge.com/)).

Ключ **никогда не хранится в репозитории**. Способы задать:

1. Вкладка **Settings** в лаунчере — сохраняется в `%APPDATA%\STlauncher\settings.json` вне репозитория.
2. Переменная окружения `CURSEFORGE_API_KEY` (приоритет у явно заданного ключа).
3. CLI: `--api-key <ключ>`.

Без ключа CurseForge-модпаки не устанавливаются; Modrinth работает всегда.
Если ключ недействителен, API вернёт `403 Forbidden`.

## Релизы и обновления

Релиз собирается автоматически по тегу `v*` через Velopack (`vpk pack` / `vpk upload`)
и публикуется в GitHub Releases. Лаунчер проверяет обновления через `GithubSource`
(см. `UpdateService.RepositoryUrl` — **замените на свой репозиторий**).
Проверка доступна только в установленной сборке; при запуске из `dotnet run`
кнопка сообщает, что обновления недоступны.

Каждый релиз содержит:

- `SHA256SUMS.txt` — контрольные суммы артефактов;
- **attestation сборки** (GitHub) — криптографическое доказательство, что файл собран
  этим workflow из этого репозитория:
  `gh attestation verify <файл> -R moh1topuk-jpg/STlauncher`.

Про подпись кода (Authenticode), почему бесплатного варианта, снимающего
предупреждение SmartScreen, не существует и что реально можно сделать —
см. [docs/SIGNING.md](docs/SIGNING.md).

## Roadmap

- [x] Ядро: метаданные, rules, загрузчик, Java, офлайн-авторизация, запуск
- [x] UI: версии, ник, память, лоадеры, моды, прогресс, консоль
- [x] `servers.dat` и пресет сервера
- [x] Мод-лоадеры и менеджер модов
- [x] Скины
- [x] Импорт модпаков Modrinth (`.mrpack`)
- [x] Импорт модпаков CurseForge (`.zip`) — нужен действующий API-ключ
- [x] Авто-обновление через Velopack
- [x] Провенанс сборок (GitHub attestation) и контрольные суммы SHA-256
- [ ] Подпись кода Authenticode — требует платного сертификата, см. [docs/SIGNING.md](docs/SIGNING.md)

## Лицензия

MIT — см. [LICENSE](LICENSE).
