# CS:S V93 — сборка фундамента

## Текущий статус

Эта инструкция описывает целевые шаги, но пока **не проверена на Windows**. Сборка DLL и запуск мода не считаются выполненными до успешной проверки в Windows с Visual Studio 2022.

## Требования

Нужны Windows 10/11, Visual Studio 2022 с workload **Desktop development with C++**, MSVC v143 для x86/x64, Windows 10 SDK 10.0.19041.0+ или Windows 11 SDK 10.0.22621.0+, а также Source SDK 2013 Multiplayer, установленный через Steam. Python для генерации проектов не требуется: `createallprojects.bat` напрямую запускает VPC.

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

До добавления CSS93 нужно проверить штатные проекты HL2MP. Соберите `Client (HL2MP)` и `Server (HL2MP)` в конфигурациях `Debug` и `Release`, платформа `Win32`. Если сборка не проходит, ошибку нужно записать в `docs/known-issues.md` и не переходить к прототипу.

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

После успешной сборки запустите `css93` через Steam или Visual Studio, выполните в консоли `map dm_lockdown`, проверьте загрузку карты и движение. Затем запустите dedicated server с параметрами `-game css93 +map dm_lockdown`, подключитесь локальным клиентом и повторите проверку.

До проведения этих действий раздел является планом проверки, а не отчётом о рабочем запуске.
