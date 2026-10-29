# Запасной путь к Modrinth и CurseForge

Если провайдер игрока блокирует или ломает Modrinth или CurseForge, моды всё равно
скачиваются: лаунчер повторяет тот же запрос через сервер владельца (`2.26.80.201`, тот же
VPS, что держит [реле](relay.md) и [второе зеркало обновлений](updates.md#второе-зеркало)).
Сервер — просто труба: файл приходит тот же, и лаунчер сверяет его хэш так же строго, как
при прямой загрузке. Кэша нет, на диске сервера ничего не остаётся.

## Что идёт через сервер

Только три вида адресов, всё остальное отвечает 404 и никуда не уходит:

| Путь на сервере | Куда | Что пропускается |
| --- | --- | --- |
| `/mr/…` | `https://cdn.modrinth.com/…` | `GET`/`HEAD` файлов вида `/data/<8 знаков>/versions/<8 знаков>/<файл>.jar\|.zip\|.mrpack` |
| `/cf/files/…` | `https://mediafilez.forgecdn.net/files/…` | `GET`/`HEAD` файлов вида `/files/<цифры>/<цифры>/<файл>.jar\|.zip\|.mrpack` |
| `/mrapi/…` | `https://api.modrinth.com/…` | `GET`/`HEAD` только тех вызовов, что делает лаунчер: `/v2/search`, `/v2/tag/category`, `/v2/project/<id>`, `/v2/project/<id>/version`, `/v2/version/<id>` |

Почему CurseForge идёт на `mediafilez`, а не на `edge.forgecdn.net`: `edge` отвечает только
редиректом 302 на `mediafilez`, и игрок пошёл бы по нему напрямую — туда, где заблокировано.
Если какой-то из CDN всё же ответит редиректом, сервер отдаёт 502 вместо того, чтобы
отправить игрока по чужому адресу.

Остальное под `/cf/` (`/cf/ping`, `/cf/v1/…` — API CurseForge с ключом) по-прежнему уходит
воркеру: прокси трогает только `/cf/files/`.

API CurseForge отдельно не нужен: он и так идёт через воркер. API Modrinth нужен: без него
лаунчер не может узнать, какой файл ставить (установка сборки Showtime спрашивает
`/v2/project/<id>/version` для каждого мода). Запись в Modrinth (`POST /v2/version_files`,
проверка обновлений по хэшам) через сервер не идёт: при блокировке проверка обновлений
просто молчит, ставить и запускать это не мешает.

### Чего у Caddy нет

Ограничения запросов с одного IP в стандартном Caddy нет (нужен плагин
`caddy-ratelimit` и своя сборка). Поэтому защита — только форма пути: через сервер можно
скачать файл мода с двух CDN и прочитать каталог Modrinth, больше ничего. Открытым прокси
он от этого не становится.

У Modrinth лимит 300 запросов в минуту с одного IP. Через сервер все игроки — один IP.
Установка сборки — 25 запросов, так что это примерно 12 одновременных установок в минуту;
сверх лимита Modrinth отвечает 429, лаунчер повторит позже.

## Как лаунчер решает

`Core/Http/CdnFallback.cs` стоит перед единственным HTTP-клиентом лаунчера, поэтому
работает везде: установка модов, модпаки, коды сборок, синхронизация каталога, буст.

- Сначала всегда прямой путь.
- Через сервер — только если прямой упал на маршруте: не найден адрес (DNS), соединение
  отказано или оборвано, не сложился TLS, нет ответа 20 секунд, файл начал качаться и
  остановился. Ещё 403 и 451 — но только если через сервер файл пришёл (CurseForge сам
  отвечает 403 на несуществующий файл).
- 404 и несовпадение хэша — это про файл, а не про путь: через сервер не повторяется.
- Если не работает ни прямой путь, ни сервер (скорее всего, нет интернета), игрок видит
  исходную ошибку, и ничего не запоминается.
- Хост, который не ответил, запоминается до конца сессии: следующие файлы идут сразу через
  сервер. Раз в 10 минут прямой путь пробуется снова.
- Один раз за сессию в консоли и строке статуса: «Modrinth недоступен напрямую, файлы идут
  через запасной путь» (или CurseForge).
- Любой другой адрес (Mojang, Fabric, GitHub, картинки модов) через сервер не идёт никогда.

Адрес сервера — `AppSettings.SecondMirrorUrl`.

## Настройка на сервере

`/etc/caddy/Caddyfile` целиком (новые блоки — до общего прокси на воркер):

```
{
	email moh1topuk@gmail.com
}

2.26.80.201 {
	tls {
		issuer acme {
			profile shortlived
		}
	}

	# --- Файлы модов: Modrinth CDN ---
	@modrinth_file {
		method GET HEAD
		path_regexp ^/mr/data/[A-Za-z0-9]{8}/versions/[A-Za-z0-9]{8}/[^/]+\.(?i:jar|zip|mrpack)$
	}
	handle @modrinth_file {
		uri strip_prefix /mr
		reverse_proxy https://cdn.modrinth.com {
			transport http {
				dial_timeout 10s
				response_header_timeout 30s
			}
			@moved status 3xx
			handle_response @moved {
				respond 502
			}
		}
	}

	# --- Файлы модов: CurseForge CDN (edge.forgecdn.net только редиректит сюда) ---
	@curseforge_file {
		method GET HEAD
		path_regexp ^/cf/files/[0-9]{1,6}/[0-9]{1,4}/[^/]+\.(?i:jar|zip|mrpack)$
	}
	handle @curseforge_file {
		uri strip_prefix /cf
		reverse_proxy https://mediafilez.forgecdn.net {
			transport http {
				dial_timeout 10s
				response_header_timeout 30s
			}
			@moved status 3xx
			handle_response @moved {
				respond 502
			}
		}
	}

	# --- API Modrinth: только чтение, только вызовы лаунчера ---
	@modrinth_api {
		method GET HEAD
		path_regexp ^/mrapi/v2/(search|tag/category|version/[A-Za-z0-9]{8}|project/[A-Za-z0-9._+!@$(),-]{1,64}(/version)?)$
	}
	handle @modrinth_api {
		uri strip_prefix /mrapi
		reverse_proxy https://api.modrinth.com {
			header_up User-Agent "STlauncher-mirror/1.0 (+https://github.com/moh1topuk-jpg/STlauncher)"
			transport http {
				dial_timeout 10s
				response_header_timeout 30s
			}
		}
	}

	# Всё остальное под этими путями никуда не уходит.
	handle /mr/* {
		respond 404
	}
	handle /mrapi/* {
		respond 404
	}
	handle /cf/files/* {
		respond 404
	}

	# Как было: всё прочее — первому зеркалу (воркеру).
	handle {
		reverse_proxy https://showtime-updates.moh1topuk.workers.dev {
			header_up Host {upstream_hostport}
		}
	}
}
```

Заметки:

- `header_up Host` для трёх новых блоков не нужен: при `https://` апстриме Caddy сам ставит
  его хост (он об этом предупреждает при проверке). У воркера строка оставлена как была.
- Range (докачка) проходит как есть. Буферизации нет: файл идёт потоком, диск не трогается.
- Тайм-ауты: 10 с на соединение с CDN, 30 с на заголовки ответа. Тело файла не ограничено
  по времени — большой файл на медленном канале не обрывается.
- `handle` взаимоисключающие, Caddy сортирует их сам; проверено `caddy adapt` — три блока с
  `path_regexp` идут первыми, общий прокси последним.

### Как выкатить

```
ssh -i ~/.ssh/stlauncher_vps root@2.26.80.201
cp /etc/caddy/Caddyfile /etc/caddy/Caddyfile.bak-$(date +%F)
nano /etc/caddy/Caddyfile              # вставить блоки выше
caddy validate --config /etc/caddy/Caddyfile
systemctl reload caddy
journalctl -u caddy -n 30 --no-pager   # без ошибок
```

Откат: `cp /etc/caddy/Caddyfile.bak-<дата> /etc/caddy/Caddyfile && systemctl reload caddy`.

### Самопроверка после выката

С любой машины (`-o /dev/null -w "%{http_code}\n"` выводит только код):

| Запрос | Ждём |
| --- | --- |
| `curl -s -o /dev/null -w "%{http_code}\n" https://2.26.80.201/mr/data/uXXizFIs/versions/Ii0gP3D8/ferritecore-8.2.0-fabric.jar` | `200` |
| `curl -s -o /dev/null -w "%{http_code}\n" -r 0-99 https://2.26.80.201/mr/data/uXXizFIs/versions/Ii0gP3D8/ferritecore-8.2.0-fabric.jar` | `206` |
| `curl -sI https://2.26.80.201/mr/data/AANobbMI/versions/g6ju16VJ/sodium-fabric-0.9.3-beta.1%2Bmc26.3.jar` | `200` (имя с `%2B`) |
| `curl -s -o /dev/null -w "%{http_code}\n" https://2.26.80.201/cf/files/8907/912/appleskin-fabric-mc26.3-3.0.10.jar` | `200` |
| `curl -s -o /dev/null -w "%{http_code}\n" "https://2.26.80.201/mrapi/v2/project/ferrite-core/version?loaders=%5B%22fabric%22%5D"` | `200` |
| `curl -s -o /dev/null -w "%{http_code}\n" https://2.26.80.201/mrapi/v2/version/Ii0gP3D8` | `200` |
| `curl -s -o /dev/null -w "%{http_code}\n" https://2.26.80.201/mr/data/uXXizFIs/icon.png` | `404` |
| `curl -s -o /dev/null -w "%{http_code}\n" https://2.26.80.201/mr/data/uXXizFIs/versions/Ii0gP3D8/evil.exe` | `404` |
| `curl -s -o /dev/null -w "%{http_code}\n" -X POST https://2.26.80.201/mr/data/uXXizFIs/versions/Ii0gP3D8/ferritecore-8.2.0-fabric.jar` | `404` |
| `curl -s -o /dev/null -w "%{http_code}\n" https://2.26.80.201/mrapi/v2/user` | `404` |
| `curl -s -o /dev/null -w "%{http_code}\n" -X POST https://2.26.80.201/mrapi/v2/version_files` | `404` |
| `curl -s -o /dev/null -w "%{http_code}\n" https://2.26.80.201/cf/files/8907/912/a.png` | `404` |
| `curl -s https://2.26.80.201/health` | `ok` (воркер на месте) |
| `curl -s -o /dev/null -w "%{http_code}\n" https://2.26.80.201/catalog.json` | `200` |

Хэш файла через сервер и напрямую совпадает:
`curl -s https://2.26.80.201/mr/data/uXXizFIs/versions/Ii0gP3D8/ferritecore-8.2.0-fabric.jar | sha1sum`
и то же с `https://cdn.modrinth.com/data/...`.

## Трафик

Установка сборки Showtime с нуля через сервер: 25 модов — 52 МБ файлов плюс 2,7 МБ списков
версий Modrinth (сжатыми; лаунчер просит сжатие с той же версии, где появился запасной
путь, без него было бы 17,6 МБ). Итого около 55 МБ, которые сервер сначала скачивает с CDN
и тут же отдаёт игроку — в счётчике провайдера это 55 МБ входящих и 55 МБ исходящих.
Обновление одного мода — 0,1–16 МБ, поиск в каталоге — десятки килобайт.

Уже установленная сборка при запуске ничего не качает: моды лежат у игрока.
Если провайдер закроет Modrinth для всех игроков из России сразу (сейчас ~64 человека в
неделю): пока ничего не меняется в каталоге, через сервер пойдут только новые установки
(55 МБ на игрока) и правки сборки (всем по объёму изменённых модов, обычно 2–10 МБ).
Худший случай — все 64 ставят сборку с нуля в один день: ~3,5 ГБ исходящих, на канале
100 Мбит/с это около пяти минут работы сервера. За месяц разумная оценка — 2–4 ГБ.
Это касается и реле на том же VPS: на время массовой загрузки канал делится с игрой.
