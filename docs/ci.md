# Windows CI

Репозиторий уже создан как приватный: [dimonshvecov2011-prog/css93](https://github.com/dimonshvecov2011-prog/css93).

Workflow находится в `.github/workflows/windows-hl2mp.yml`. Он запускается на `windows-latest`, переходит в `mp/src` и вызывает `devtools\bin\vpc.exe /hl2mp +everything /define:VS2022`, без `/mksln`. Затем MSBuild отдельно собирает проекты в конфигурации `Release|Win32`. Логи, DLL и PDB сохраняются как GitHub Actions artifact.

## Результаты

Первый run [#45149824180](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45149824180) завершился ошибкой старого режима `/mksln`: `ERROR: Unable to find RegKey for .vcproj or .vcxproj files in solutions.` VPC успел создать `.vcxproj`, но не смог зарегистрировать legacy solution на runner с Visual Studio 2022. Поэтому CI использует прямую сборку `.vcxproj` без `/mksln`.

Штатный HL2MP run [#45150072071](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45150072071) успешно собрал `Client (HL2MP)` и `Server (HL2MP)` в `Release|Win32`.

CSS93 run [#45151530011](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45151530011) успешно прошёл все этапы: VPC распознал `client_css93.vpc` и `server_css93.vpc`, проверка нашла `<PlatformToolset>v143</PlatformToolset>`, а `Client (HL2MP)`, `Server (HL2MP)`, `Client (CSS93)` и `Server (CSS93)` собрались в `Release|Win32`.

Диагностический run [#45161557652](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45161557652) также успешен. Artifact `windows-hl2mp-build-logs` теперь содержит:

- `mp/game/mod_hl2mp/bin/client.dll` и `server.dll` — штатная HL2MP-пара для A/B-теста;
- `mp/game/css93/bin/client.dll` и `server.dll` — CSS93-пара;
- `Release_mod_hl2mp/client.pdb`, `server.pdb` и соответствующие `vc143.pdb`;
- `Release_css93/client.pdb`, `server.pdb` и соответствующие `vc143.pdb`;
- `vpc-generation.log` и `build-release-win32.log`.

Страница artifact: [run #45161557652 artifacts](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45161557652/artifacts).

## SearchPaths и запуск

Пользовательская Windows-проверка подтвердила запуск мода, отображение карт HL2DM, загрузку `dm_lockdown` и подключение клиента. После `Initializing renderer` происходит падение с `EXCEPTION_ACCESS_VIOLATION` в `engine.dll`. В консоли также отсутствовали базовые HL2/HL2DM материалы. Поэтому текущий приоритет — исправленный официальный набор SearchPaths и проверка наличия установленного Half-Life 2: Deathmatch, а не немедленное изменение клиентского линкера.

## Почему VPC пишет Visual Studio 2013

Строка `Generating for Visual Studio 2013` — сообщение старого генератора VPC о формате генерируемого проекта. Она не означает, что выбран VS2013 toolset. Фактический результат проверяется по XML `.vcxproj`: CSS93 проекты содержат `PlatformToolset` `v143`, то есть используется toolset Visual Studio 2022.

## `/FORCE` и `_hypot`

В логе Client (CSS93) есть ожидаемые предупреждения:

```text
libucrt.lib(hypot.obj) : warning LNK4006: _hypot already defined in particles.lib(particle_sort.obj); second definition ignored
client.dll : warning LNK4088: image being generated due to /FORCE option; image may not run
```

В `mp/src/game/client/client_base.vpc` для `[$VS2022]` явно задан `/FORCE:MULTIPLE`. Он нужен из-за дублирования `_hypot` между `particles.lib` и VS2022 UCRT (`libucrt.lib`). До результата A/B-теста workaround не удаляется. Если штатные HL2MP DLL запустятся без падения, следующим отдельным шагом будет Debug-сборка CSS93 без `/FORCE:MULTIPLE` с PDB и анализ дампа.
