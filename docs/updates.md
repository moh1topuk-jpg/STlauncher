# Выпуск версий и обновления

## Как выпускается версия

1. Изменения описаны в [`CHANGELOG.md`](../CHANGELOG.md) под заголовком
   `## [X.Y.Z] — ГГГГ-ММ-ДД` (раздел «Не выпущено» переименовывается в новую версию).
2. В [`src/STlauncher.App/STlauncher.App.csproj`](../src/STlauncher.App/STlauncher.App.csproj)
   поднят `<Version>`.
3. Изменения в `main`, CI зелёный.
4. Поставлен и отправлен тег:

   ```powershell
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```

Дальше всё делает [CI](../.github/workflows/ci.yml): собирает лаунчер, упаковывает
через Velopack, публикует релиз на GitHub с текстом из `CHANGELOG.md`, прикладывает
`SHA256SUMS.txt` и attestation. **Если в `CHANGELOG.md` нет раздела для этой версии,
релиз не собирается** — так список изменений не отстаёт от версий.

Установленные лаунчеры находят новую версию сами: при запуске и каждые шесть часов.

## Зеркало обновлений

У части игроков GitHub недоступен: провайдер обрывает TLS-соединение, и обновление с
GitHub не приходит. Поэтому обновления и каталог отдаются ещё и с Cloudflare:

```
https://showtime-updates.moh1topuk.workers.dev/
```

Воркер [`workers/updates/worker.js`](../workers/updates/worker.js) сам забирает с GitHub
последний релиз и отдаёт его файлы — **после выпуска версии ничего заливать не нужно**.

| Путь | Что отдаёт |
| --- | --- |
| `/releases.win.json`, `/RELEASES`, `/*.nupkg` | фид и пакеты обновления |
| `/STlauncher-win-Setup.exe` | установщик последней версии — ссылка для игроков |
| `/catalog.json` | каталог из `main` |
| `/health` | `ok` |

Как лаунчер его находит:

- адрес фида задан в `catalog.json` полем `updateFeedUrl`. Убрать поле — лаунчер вернётся
  к GitHub, новая версия для этого не нужна;
- если не загрузился сам каталог (GitHub закрыт полностью), лаунчер берёт его с зеркала.
  Этот адрес зашит в лаунчер (`AppSettings.CatalogMirrorUrl`) и используется только для
  стандартного каталога.

Если недоступно и зеркало, лаунчер показывает понятную ошибку и кнопку «Скачать вручную».
Переустановка поверх старой версии сборки и настройки не трогает.

### Развернуть зеркало заново

Нужен бесплатный аккаунт Cloudflare, DNS трогать не нужно.

1. *Workers & Pages* → *Create* → *Start with Hello World!* → имя `showtime-updates` → *Deploy*.
2. *Edit code* → вставить [`workers/updates/worker.js`](../workers/updates/worker.js) → *Deploy*.
3. *Settings* → *Variables and Secrets*:

   | Тип | Имя | Значение |
   | --- | --- | --- |
   | Text | `REPO` | `moh1topuk-jpg/STlauncher` |
   | Secret | `TOKEN` | только если репозиторий приватный |

4. Проверить: `/health` отвечает `ok`, `/releases.win.json` — JSON с последней версией.
5. Если адрес изменился — поправить `updateFeedUrl` в `catalog.json` и
   `AppSettings.CatalogMirrorUrl`.
