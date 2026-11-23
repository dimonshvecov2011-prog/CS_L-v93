# CS:S V93 — сборка фундамента

## Текущий статус

Сборка Client/Server CSS93 в GitHub Actions на Windows с Visual Studio 2022 подтверждена. На пользовательском Windows-ПК мод запускается, карты HL2DM видны, `dm_lockdown` загружается и клиент подключается. Полный игровой запуск пока не завершён: после `Initializing renderer` наблюдается падение, которое диагностируется отдельно.

## Требования

Нужны Windows 10/11, Visual Studio 2022 с workload **Desktop development with C++**, MSVC v143 для x86/x64, Windows 10 SDK 10.0.19041.0+ или Windows 11 SDK 10.0.22621.0+, а также установленный через Steam **Half-Life 2: Deathmatch** с базовым HL2-контентом. Python для генерации проектов не требуется: `createallprojects.bat` напрямую запускает VPC.

## Получение исходников

Проект использует форк `Source-SDK-Resources/source-sdk-vs2022`, потому что именно в нём multiplayer-исходники находятся в `mp/src`, а генератор проектов — в `mp/src/createallprojects.bat`.

```bat
git clone https://github.com/Source-SDK-Resources/source-sdk-vs2022 css93
cd css93\mp\src
createallprojects.bat
```

Команда создаёт `mp/src/everything.sln`. Откройте это решение в Visual Studio 2022.

Файлы `*.sln` и `*.vcxproj` намеренно не хранятся в Git: они генерируются VPC из исходных `.vpc` и `.vgc` после запуска `createallprojects.bat`. Поэтому отсутствие решения в checkout является ожидаемым поведением, а не пропущенным файлом.

## Штатная сборка SDK без изменений

До добавления CSS93 следует проверить штатные проекты HL2MP. Соберите `Client (HL2MP)` и `Server (HL2MP)` в конфигурациях `Debug` и `Release`, платформа `Win32`. В CI подтверждена сборка `Release|Win32`; Debug и локальная Windows-сборка ещё не выполнялись.

## Проекты CSS93

После успешной штатной сборки зарегистрируйте `Client (CSS93)` и `Server (CSS93)` в VPC на основе `mp/src/game/client/client_hl2mp.vpc` и `mp/src/game/server/server_hl2mp.vpc`. Добавьте новые VPC-файлы в `mp/src/vpc_scripts/projects.vgc` и соответствующие группы в `mp/src/vpc_scripts/groups.vgc`, затем заново выполните `createallprojects.bat`.

Выходные каталоги должны направлять DLL в `mp/game/css93/bin`. Имена файлов должны быть ровно `client.dll` и `server.dll`.

## Установка мода

Единственный поддерживаемый вариант установки:

```text
...\steamapps\sourcemods\css93\gameinfo.txt
```

После копирования собранных DLL в `css93\bin` перезапустите Steam.

## Проверка на Windows

Запустите `css93` через Steam или Visual Studio и выполните в консоли `map dm_lockdown`. Для dedicated server используйте `-game css93 +map dm_lockdown`, затем подключитесь локальным клиентом.

В текущем тесте карта загружается и подключение проходит, но затем клиент падает после `Initializing renderer`. При диагностике сначала проверьте, что установлен Half-Life 2: Deathmatch, а в `gameinfo.txt` используются относительные `hl2mp/...` и `|all_source_engine_paths|hl2/...` SearchPaths из официального HL2MP-шаблона. Если проблема сохранится, замените CSS93 DLL на штатные HL2MP DLL из диагностического artifact и повторите тест.
