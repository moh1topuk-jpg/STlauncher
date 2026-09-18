# Статистика онлайна

Раздел «Сервер» показывает онлайн за сутки и неделю, средний онлайн, рекорд, аптайм и
место в рейтинге Top-Minecrafter. Сам лаунчер может замерять онлайн только пока открыт,
поэтому историю круглосуточно собирает отдельный сборщик — Cloudflare Worker
[`workers/stats/worker.js`](../workers/stats/worker.js) — и публикует одним JSON:

```
https://showtime-stats.moh1topuk.workers.dev/
```

Адрес задаётся в `catalog.json` полем `serverStatsUrl`, поэтому сборщик можно перенести
или отключить без новой версии лаунчера.

## Почему свой сборщик

Готового бесплатного API с историей онлайна нет: у minecraft-statistic.net API отключён,
LiteByte выдаёт ключ на посетителя с лимитом 10 запросов в минуту, mcstatus.io и похожие
отдают только текущий пинг. Top-Minecrafter даёт средний онлайн, рекорд, аптайм и место в
рейтинге, но **его ключ нельзя класть в лаунчер**: тем же ключом открываются списки
проголосовавших и донатов, а репозиторий публичный. Поэтому ключ живёт только в секрете
воркера, а игроки читают готовый JSON без ключа.

## Если сборщик недоступен

1. Нет ответа — лаунчер берёт сохранённую копию (`meta/server-stats.json`).
2. Копии нет или она старше 6 часов — график строится по собственным замерам лаунчера.
3. Адрес не задан — только собственные замеры.

## Что отдаёт

```json
{
  "schemaVersion": 1,
  "updatedAt": "2026-09-17T21:30:00.000Z",
  "server":  { "name": "ShowTime", "address": "mc.showtime.su", "online": 33, "max": 2026, "isOnline": true },
  "summary": { "averageWeek": 40, "peak": 366, "peakAt": "2026-08-07T20:49:32.000Z", "uptime": 100, "rank": 26 },
  "ranges":  {
    "day":  { "buckets": [ { "t": "…", "avg": 34.2, "peak": 41 } ] },
    "week": { "buckets": [ { "t": "…", "avg": null, "peak": null } ] }
  }
}
```

`avg: null` — пропуск (сборщик в этот час не работал), а не ноль игроков. Лаунчер рисует
их по-разному.

## Развернуть заново

Нужен бесплатный аккаунт Cloudflare, DNS трогать не нужно.

1. *Workers & Pages* → *Create* → *Start with Hello World!* → имя `showtime-stats` → *Deploy*.
2. *Edit code* → вставить [`workers/stats/worker.js`](../workers/stats/worker.js) → *Deploy*.
3. *Storage & Databases* → *KV* → *Create Instance* → `stlauncher-stats`.
4. Воркер → *Settings* → *Bindings* → *Add* → *KV namespace*: имя переменной `STATS`,
   хранилище `stlauncher-stats`.
5. *Settings* → *Variables and Secrets*:

   | Тип | Имя | Значение |
   | --- | --- | --- |
   | **Secret** | `TOPMC_KEY` | ключ из личного кабинета Top-Minecrafter |
   | Text | `SERVER_ID` | `6689` |
   | Text | `SERVER_ADDRESS` | `mc.showtime.su` |

6. *Settings* → *Trigger Events* → *Cron Trigger* → `*/5 * * * *`.
7. Открыть адрес воркера: сразу после создания `sampleCount` равен 0, через минуту
   появятся данные.
8. Если адрес изменился — поправить `serverStatsUrl` в `catalog.json`.

Бесплатного плана хватает с запасом: около 576 записей в KV в сутки при лимите 1000,
один запрос к воркеру на запуск лаунчера, хранится 8 суток замеров.

## Если ключ утёк

В личном кабинете Top-Minecrafter нажать «Перевыпустить» и обновить секрет `TOPMC_KEY` в
воркере. Лаунчеры трогать не нужно.
