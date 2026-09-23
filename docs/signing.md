# Подпись установщика

Установщик лаунчера не подписан, поэтому Windows показывает SmartScreen: «Система
Windows защитила ваш компьютер». SmartScreen не проверяет код — только подпись
издателя и то, сколько людей уже запускали ровно этот файл. Отсюда два рычага.

## 1. Замороженный установщик (уже работает)

Зеркало отдаёт по `/STlauncher-win-Setup.exe` установщик одного закреплённого релиза
(поле `installerRelease` в `catalog.json`), а не последнего. Файл не меняется от релиза
к релизу, и репутация копится на нём. Установленный лаунчер сам обновляется при первом
запуске. Подробнее — [updates.md](updates.md).

Менять `installerRelease` стоит только когда появится подписанный установщик или когда
закреплённый перестанет ставиться: каждая смена обнуляет накопленное.

## 2. Бесплатная подпись через SignPath Foundation

[SignPath Foundation](https://signpath.org/) подписывает сборки open-source проектов
бесплатно. STlauncher подходит: лицензия MIT, публичный репозиторий, сборка в GitHub
Actions, релизы через теги.

Заявку подаёт владелец проекта на [signpath.org/apply](https://signpath.org/apply).
Что ответить в форме:

| Поле | Что писать |
| --- | --- |
| Project name | STlauncher |
| Project URL | https://github.com/moh1topuk-jpg/STlauncher |
| License | MIT |
| Description | Open-source Minecraft launcher for the mc.showtime.su community server: installs the game, Java and the server's recommended mod build, keeps them updated, manages mods, resource packs and shaders. Windows desktop app (.NET 8, Avalonia). Distributed as a Velopack installer via GitHub Releases. |
| Build system | GitHub Actions (`.github/workflows/ci.yml`), release on tag `vX.Y.Z` |
| Artifacts to sign | `STlauncher-win-Setup.exe`, `STlauncher.App.exe` (inside the Velopack package), `Update.exe` |
| Privacy policy | https://github.com/moh1topuk-jpg/STlauncher/blob/main/docs/privacy.md |
| Users / downloads | по данным [страницы статистики](monitoring.md): около 10 активных установок в день |
| Team | один мейнтейнер (владелец репозитория); code review — pull requests в `main` |

Зачем нужна политика конфиденциальности: лаунчер отправляет анонимную статистику
запусков, а форма требует политику для любого ПО, которое «собирает данные».

После одобрения SignPath выдаёт проект в своей системе и GitHub Action
`signpath/github-action-submit-signing-request`. Подпись встраивается в CI так:

1. `vpk pack` собирает пакет без подписи, как сейчас.
2. Шаг SignPath отправляет `STlauncher-win-Setup.exe` и распакованные exe на подпись,
   ждёт результата и подменяет файлы подписанными. Velopack советует подписывать до
   упаковки (`--signTemplate`), поэтому, скорее всего, порядок будет: publish → подпись
   `STlauncher.App.exe` → `vpk pack` с уже подписанным exe → подпись `Setup.exe`.
3. Хэши и attestation считаются по подписанным файлам.
4. `installerRelease` в каталоге переводится на первый подписанный релиз.

Точный порядок шагов зависит от того, какие артефакты SignPath согласится
подписывать, — уточняется после одобрения заявки.
