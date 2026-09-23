# Политика конфиденциальности STlauncher / Privacy Policy

*Обновлено: 23 сентября 2026. English version below.*

STlauncher — лаунчер Minecraft для сервера mc.showtime.su с открытым исходным кодом
([github.com/moh1topuk-jpg/STlauncher](https://github.com/moh1topuk-jpg/STlauncher)).
У лаунчера нет аккаунтов, регистрации и рекламы. Ниже — всё, что он отправляет в сеть,
и зачем.

## Что лаунчер отправляет

**Анонимная статистика запусков.** Раз за запуск лаунчер отправляет на сборщик
статистики владельца сервера (Cloudflare Worker) следующее:

- случайный идентификатор установки (создаётся при первом запуске, ни с чем не связан);
- версию лаунчера, язык интерфейса и версию ОС;
- как прошла проверка обновлений (например, «успешно через зеркало»);
- если игра закрылась с ошибкой — причину падения, версию игры и загрузчик модов.

**Не отправляются:** ник, пароли, файлы, пути на диске, содержимое папок, IP-адрес
в явном виде, история игры. У мода-виновника падения передаётся только его название.

Статистика нужна владельцу сервера, чтобы видеть, сколько людей пользуются лаунчером,
доходят ли до них обновления и что ломается. Данные хранятся в агрегированном виде
(счётчики за день и неделю) и не продаются и не передаются третьим лицам.

**Как отключить:** Настройки → «Анонимная статистика запусков». После этого лаунчер
не отправляет ничего из перечисленного.

## Сторонние сервисы

Чтобы работать, лаунчер обращается к сторонним сервисам. Они видят ваш IP-адрес, как
любой сайт, который вы открываете:

| Сервис | Зачем | Что передаётся |
| --- | --- | --- |
| Mojang / Microsoft (`piston-meta.mojang.com`, `resources.download.minecraft.net`, `libraries.minecraft.net`) | Скачать игру, библиотеки и ресурсы | Только запросы файлов |
| Fabric, Quilt, Forge, NeoForge | Скачать загрузчик модов | Только запросы файлов |
| Adoptium (`api.adoptium.net`) | Скачать Java | Только запросы файлов |
| Modrinth (`api.modrinth.com`) | Каталог модов, ресурспаков и шейдеров; проверка обновлений модов | Поисковые запросы; хэши файлов модов при проверке обновлений |
| GitHub и зеркало на Cloudflare | Обновления лаунчера и каталог сервера | Версия лаунчера |
| Mojang, TLauncher, ely.by, mc-heads.net, minotar.net | Показать скин по нику | Ник, который вы ввели |
| mc.showtime.su | Онлайн сервера на главном экране | Обычный запрос статуса Minecraft |
| Discord (локально, через приложение Discord на вашем компьютере) | Статус «Играет на Showtime» | Название сервера; включается отдельно в настройках |

Лаунчер не запрашивает данные аккаунта Microsoft/Mojang и не хранит пароли.

## Что хранится на вашем компьютере

Настройки, ник, сборки, моды, миры, логи и кэш скинов лежат в папке данных лаунчера
(по умолчанию `%APPDATA%\STlauncher`; папку можно перенести в настройках). Лог лаунчера
(`logs/launcher.log`) никуда не отправляется автоматически — только если вы сами
решите его прислать.

## Дети

Лаунчер не собирает персональные данные и не различает возраст пользователей.

## Изменения и контакты

Изменения политики фиксируются в истории этого файла в репозитории. Вопросы —
через [Issues](https://github.com/moh1topuk-jpg/STlauncher/issues) на GitHub.

---

# Privacy Policy (English)

*Last updated: September 23, 2026.*

STlauncher is an open-source Minecraft launcher for the mc.showtime.su server
([github.com/moh1topuk-jpg/STlauncher](https://github.com/moh1topuk-jpg/STlauncher)).
It has no accounts, no sign-up and no ads. This page lists everything it sends over the
network and why.

## What the launcher sends

**Anonymous usage statistics.** Once per start the launcher sends the following to the
server owner's statistics collector (a Cloudflare Worker):

- a random install identifier, generated on first start and linked to nothing else;
- launcher version, interface language and OS version;
- how the update check went (for example "ok via mirror");
- if the game crashed: the crash cause, game version and mod loader.

**Never sent:** nickname, passwords, files, paths on disk, folder contents, play history.
For a mod that caused a crash only its name is sent.

The statistics let the server owner see how many people use the launcher, whether
updates reach them and what breaks. The data is kept in aggregated form (daily and
weekly counters) and is neither sold nor shared with third parties.

**Opting out:** Settings → "Anonymous launch statistics". After that the launcher sends
nothing listed above.

## Third-party services

To work at all, the launcher contacts third-party services. Like any website you open,
they see your IP address:

| Service | Purpose | What is sent |
| --- | --- | --- |
| Mojang / Microsoft (`piston-meta.mojang.com`, `resources.download.minecraft.net`, `libraries.minecraft.net`) | Download the game, libraries and assets | File requests only |
| Fabric, Quilt, Forge, NeoForge | Download the mod loader | File requests only |
| Adoptium (`api.adoptium.net`) | Download Java | File requests only |
| Modrinth (`api.modrinth.com`) | Catalog of mods, resource packs and shaders; mod update checks | Search queries; SHA-1 hashes of mod files during update checks |
| GitHub and a Cloudflare mirror | Launcher updates and the server catalog | Launcher version |
| Mojang, TLauncher, ely.by, mc-heads.net, minotar.net | Show the skin for a nickname | The nickname you typed |
| mc.showtime.su | Server status on the main screen | A regular Minecraft status ping |
| Discord (locally, through the Discord app on your computer) | "Playing on Showtime" status | Server name; enabled separately in settings |

The launcher never asks for Microsoft/Mojang account credentials and stores no passwords.

## What stays on your computer

Settings, nickname, builds, mods, worlds, logs and the skin cache live in the launcher's
data folder (`%APPDATA%\STlauncher` by default; it can be moved in settings). The
launcher log (`logs/launcher.log`) is never uploaded automatically.

## Children

The launcher collects no personal data and does not distinguish users by age.

## Changes and contact

Changes to this policy are visible in this file's history in the repository. Questions go
to [GitHub Issues](https://github.com/moh1topuk-jpg/STlauncher/issues).
