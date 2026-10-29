# Политика конфиденциальности STlauncher / Privacy Policy

*Обновлено: 29 октября 2026. English version below.*

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
- сколько раз интерфейс лаунчера зависал за последнюю неделю — только число; сами отчёты
  о зависаниях остаются на компьютере;
- если игра закрылась с ошибкой — отдельное сообщение о падении (см. ниже).

**Сообщение о падении игры** содержит ровно это:

- тот же случайный идентификатор установки, версию лаунчера и ОС;
- причину падения одним словом из списка лаунчера (например, `MissingDependency`,
  `GraphicsDriverCrash`, `Unknown`);
- версию игры и загрузчик модов (например, `1.21.4 Fabric`);
- код, с которым закрылась игра (число);
- уточнение, если оно есть: название мода-виновника; для падения, которое остановила
  Windows, — её код ошибки и его вид (`0xC0000005 AccessViolation`); для падения в
  видеодрайвере — только производителя (AMD, NVIDIA, Intel); для падения в оверлее —
  название программы (Discord, OBS, Steam, MSI Afterburner);
- «сигнатуру» падения, если уточнения нет (например, причина не определена) или игру остановила Windows: имя класса ошибки Java
  (`java.lang.NullPointerException`) и id мода из стека вызовов, если это мод вашей
  сборки (`@sodium`), либо вид нативной ошибки и тип библиотеки, где она случилась
  (`EXCEPTION_ACCESS_VIOLATION in amd-driver`). Не длиннее 120 символов.

Из логов и отчётов о падении **не передаётся** ничего, кроме перечисленного: ни текст
ошибок, ни строки стека вызовов, ни пути, ни имена файлов, ни ник, ни адреса серверов.
Сборщик дополнительно отбрасывает сигнатуру, если она не похожа на имя класса с id мода.

**Не отправляются:** ник, пароли, файлы, пути на диске, содержимое папок, IP-адрес
в явном виде, история игры.

Статистика нужна владельцу сервера, чтобы видеть, сколько людей пользуются лаунчером,
доходят ли до них обновления и что ломается. Данные хранятся в агрегированном виде
(счётчики за день и неделю) и не продаются и не передаются третьим лицам.

**Как отключить:** Настройки → «Анонимная статистика запусков». После этого лаунчер
не отправляет ничего из перечисленного.

**Сборка, которой вы поделились коротким кодом.** Когда вы нажимаете «Поделиться» у
сборки, лаунчер отправляет на свой сервер её название и список модов: версию игры,
загрузчик, для каждого файла — его место в сборке, адрес скачивания, хэш и размер, а для
файлов, которых нет на Modrinth, — только имя файла. Взамен сервер выдаёт короткий код
(`SB-XXXXX-XXXXX`), по которому друг получает этот список. Ник, пути на диске и сами
файлы не отправляются. Список хранится на сервере 180 дней с последнего раза, когда им
поделились или получили его по коду, после чего удаляется; сервер не записывает, кто и с
какого адреса поделился сборкой. Перед первой отправкой лаунчер говорит об этом и ждёт
вашего согласия. Если сервер не нужен, в меню сборки есть «Скопировать длинный код»: он
содержит то же самое, но никуда не отправляется.

**Отчёт, который вы отправили админу.** Только когда вы сами нажимаете «Отправить админу»
и затем «Отправить», лаунчер отправляет отчёт двумя путями. Первый — архив в Telegram
владельца сервера: логи лаунчера и игры, отчёт о падении, отчёты о зависаниях, список
модов сборки, настройки лаунчера, сведения о компьютере, ваш ник и ваш комментарий.
Второй — то же самое одним текстом на сервер лаунчера, чтобы разработчик нашёл причину;
перед отправкой из текста убираются путь к вашей папке пользователя, имя пользователя
Windows, токены и идентификаторы аккаунта и чужие IP-адреса. Ник остаётся. Этот текст
хранится на сервере 30 дней и удаляется; по сети его получить нельзя, его читают только на
самом сервере. Взамен вы получаете номер отчёта (`R-XXXXX`), чтобы назвать его
администратору. Сервер не записывает, с какого адреса пришёл отчёт. Если сервер лаунчера
недоступен, отчёт уходит только в Telegram.

**Проверка «Почему лагает?».** Кнопка в настройках и на странице сервера измеряет, как
быстро отвечают сервер Showtime (обычный запрос статуса Minecraft), сервер лаунчера,
зеркала обновлений, Modrinth и CurseForge через зеркало, и сколько ищется адрес сервера.
Это обычные запросы к этим узлам; результаты остаются на экране и никуда не отправляются.
«Скопировать результат» кладёт их в буфер обмена, и дальше решаете вы.

## Сторонние сервисы

Чтобы работать, лаунчер обращается к сторонним сервисам. Они видят ваш IP-адрес, как
любой сайт, который вы открываете:

| Сервис | Зачем | Что передаётся |
| --- | --- | --- |
| Mojang / Microsoft (`piston-meta.mojang.com`, `resources.download.minecraft.net`, `libraries.minecraft.net`) | Скачать игру, библиотеки и ресурсы | Только запросы файлов |
| Fabric, Quilt, Forge, NeoForge | Скачать загрузчик модов | Только запросы файлов |
| Adoptium (`api.adoptium.net`) | Скачать Java | Только запросы файлов |
| Modrinth (`api.modrinth.com`) | Каталог модов, ресурспаков и шейдеров; проверка обновлений модов | Поисковые запросы; хэши файлов модов при проверке обновлений |
| Сервер владельца (`2.26.80.201`) — запасной путь к Modrinth и CurseForge | Только если прямой путь к ним не работает (блокировка у провайдера): файлы модов и запросы к каталогу Modrinth идут через него, см. [mirror.md](mirror.md) | Адрес файла или запрос каталога — то же, что ушло бы напрямую; ваш IP виден серверу владельца, как и любому сайту |
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

Там же лаунчер ведёт записи о собственных сбоях: `crash.log` (необработанные ошибки),
папку `freezes` (до пяти отчётов о зависаниях интерфейса: когда, как долго, версия
лаунчера и ОС, какой раздел был открыт и какие операции шли) и `startup-state.json`
(удалось ли лаунчеру открыться в прошлый раз). Эти файлы попадают в отчёт для
администратора, только когда вы сами его собираете или отправляете. Если прошлый запуск
не удался, лаунчер спросит, переустановить ли его файлы, и ничего не скачает без вашего
согласия.

## Дети

Лаунчер не собирает персональные данные и не различает возраст пользователей.

## Изменения и контакты

Изменения политики фиксируются в истории этого файла в репозитории. Вопросы —
через [Issues](https://github.com/moh1topuk-jpg/STlauncher/issues) на GitHub.

---

# Privacy Policy (English)

*Last updated: October 29, 2026.*

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
- how many times the launcher's interface froze over the last week: the number only; the
  freeze reports themselves stay on the computer;
- if the game crashed: a separate crash message (see below).

**The crash message** carries exactly this:

- the same random install identifier, launcher version and OS;
- the crash cause as one word from the launcher's list (for example `MissingDependency`,
  `GraphicsDriverCrash`, `Unknown`);
- the game version and mod loader (for example `1.21.4 Fabric`);
- the code the game exited with (a number);
- a detail, when there is one: the name of the mod at fault; for a crash Windows stopped,
  its error code and kind (`0xC0000005 AccessViolation`); for a crash in the graphics
  driver, only the vendor (AMD, NVIDIA, Intel); for a crash in an overlay, the program's
  name (Discord, OBS, Steam, MSI Afterburner);
- a crash "signature" when there is no detail (for example, the cause is not known) or Windows stopped the game: the Java error's class name
  (`java.lang.NullPointerException`) and the id of the mod in the call stack if it is a
  mod of your build (`@sodium`), or the kind of native error and the type of library it
  happened in (`EXCEPTION_ACCESS_VIOLATION in amd-driver`). At most 120 characters.

Nothing else from logs and crash reports **is sent**: no error text, no stack lines, no
paths, no file names, no nickname, no server addresses. The collector also drops a
signature that does not look like a class name with a mod id.

**Never sent:** nickname, passwords, files, paths on disk, folder contents, play history.

The statistics let the server owner see how many people use the launcher, whether
updates reach them and what breaks. The data is kept in aggregated form (daily and
weekly counters) and is neither sold nor shared with third parties.

**Opting out:** Settings → "Anonymous launch statistics". After that the launcher sends
nothing listed above.

**A build you shared by a short code.** When you press "Share" on a build, the launcher
sends its name and list of mods to the launcher's own server: the game version, the mod
loader, and for each file its place in the build, download address, hash and size; for
files that are not on Modrinth, the file name only. In return the server gives a short
code (`SB-XXXXX-XXXXX`) by which a friend gets that list. Your nickname, paths on disk
and the files themselves are not sent. The list is kept on the server for 180 days after
it was last shared or fetched by its code and is then deleted; the server does not record
who shared a build or from which address. Before the first upload the launcher says so
and waits for your consent. If you would rather not use the server, the build's menu has
"Copy the long code": it holds the same and is sent nowhere.

**A report you sent to the admin.** Only when you press "Send to the admin" and then
"Send" does the launcher send a report, two ways. First, a zip to the server owner's
Telegram: launcher and game logs, the crash report, freeze reports, the build's mod list,
launcher settings, what the computer is, your nickname and your comment. Second, the same
as one text to the launcher's server, so the developer can find the cause; before it is
sent, the path of your user folder, your Windows user name, tokens and account ids, and
other people's IP addresses are taken out of it. The nickname stays. That text is kept on
the server for 30 days and then deleted; it cannot be fetched over the network and is read
only on the server itself. You get a report number (`R-XXXXX`) to quote to the admin. The
server does not record which address a report came from. If the launcher's server cannot
be reached, the report goes to Telegram only.

**The "Why does it lag?" check.** The button in Settings and on the server page measures
how fast the Showtime server (a regular Minecraft status ping), the launcher's server, the
update mirrors, Modrinth and CurseForge through the mirror answer, and how long the
server's address takes to resolve. These are ordinary requests to those places; the
results stay on screen and are sent nowhere. "Copy the result" puts them in the clipboard,
and the rest is up to you.

## Third-party services

To work at all, the launcher contacts third-party services. Like any website you open,
they see your IP address:

| Service | Purpose | What is sent |
| --- | --- | --- |
| Mojang / Microsoft (`piston-meta.mojang.com`, `resources.download.minecraft.net`, `libraries.minecraft.net`) | Download the game, libraries and assets | File requests only |
| Fabric, Quilt, Forge, NeoForge | Download the mod loader | File requests only |
| Adoptium (`api.adoptium.net`) | Download Java | File requests only |
| Modrinth (`api.modrinth.com`) | Catalog of mods, resource packs and shaders; mod update checks | Search queries; SHA-1 hashes of mod files during update checks |
| The owner's server (`2.26.80.201`) as a backup route to Modrinth and CurseForge | Only when the direct route to them fails (blocked by the provider): mod files and Modrinth catalog requests go through it, see [mirror.md](mirror.md) | The file address or catalog query, the same as would go directly; your IP is visible to the owner's server, as to any site |
| GitHub and a Cloudflare mirror | Launcher updates and the server catalog | Launcher version |
| Mojang, TLauncher, ely.by, mc-heads.net, minotar.net | Show the skin for a nickname | The nickname you typed |
| mc.showtime.su | Server status on the main screen | A regular Minecraft status ping |
| Discord (locally, through the Discord app on your computer) | "Playing on Showtime" status | Server name; enabled separately in settings |

The launcher never asks for Microsoft/Mojang account credentials and stores no passwords.

## What stays on your computer

Settings, nickname, builds, mods, worlds, logs and the skin cache live in the launcher's
data folder (`%APPDATA%\STlauncher` by default; it can be moved in settings). The
launcher log (`logs/launcher.log`) is never uploaded automatically.

The launcher also keeps records of its own failures there: `crash.log` (unhandled
errors), the `freezes` folder (up to five reports of the interface freezing: when, for
how long, launcher version and OS, which section was open and which operations were
running) and `startup-state.json` (whether the launcher managed to open last time).
These files go into the report for the admin only when you collect or send it yourself.
If the previous start failed, the launcher asks whether to reinstall its files and
downloads nothing without your consent.

## Children

The launcher collects no personal data and does not distinguish users by age.

## Changes and contact

Changes to this policy are visible in this file's history in the repository. Questions go
to [GitHub Issues](https://github.com/moh1topuk-jpg/STlauncher/issues).
