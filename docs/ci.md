# Windows CI

Репозиторий уже создан как приватный: [dimonshvecov2011-prog/css93](https://github.com/dimonshvecov2011-prog/css93).

Workflow находится в `.github/workflows/windows-hl2mp.yml`. Он запускается на `windows-latest`, переходит в `mp/src` и вызывает `devtools\bin\vpc.exe /hl2mp +everything /define:VS2022`, без `/mksln`. Затем MSBuild отдельно собирает проекты в конфигурации `Release|Win32`. Логи и собранные CSS93 DLL сохраняются как GitHub Actions artifact.

## Результаты

Первый run [#45149824180](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45149824180) завершился ошибкой старого режима `/mksln`: `ERROR: Unable to find RegKey for .vcproj or .vcxproj files in solutions.` VPC успел создать `.vcxproj`, но не смог зарегистрировать legacy solution на runner с Visual Studio 2022. Поэтому CI использует прямую сборку `.vcxproj` без `/mksln`.

Штатный HL2MP run [#45150072071](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45150072071) успешно собрал `Client (HL2MP)` и `Server (HL2MP)` в `Release|Win32`.

CSS93 run [#45151530011](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45151530011) успешно прошёл все этапы:

- VPC распознал `client_css93.vpc` и `server_css93.vpc` и создал проекты `Client (CSS93)` и `Server (CSS93)`.
- Проверка сгенерированных файлов нашла `<PlatformToolset>v143</PlatformToolset>` во всех CSS93 project configurations. Таким образом, `/define:VS2022` применяется корректно.
- Успешно собраны `Client (HL2MP)`, `Server (HL2MP)`, `Client (CSS93)` и `Server (CSS93)` в `Release|Win32`.
- CSS93 DLL опубликованы в `mp/game/css93/bin/client.dll` и `mp/game/css93/bin/server.dll`.

Artifact `windows-hl2mp-build-logs` доступен на [странице artifacts run #45151530011](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45151530011/artifacts). В нём находятся `vpc-generation.log`, `build-release-win32.log` и обе CSS93 DLL.

## Почему VPC пишет Visual Studio 2013

Строка `Generating for Visual Studio 2013` — сообщение старого генератора VPC о формате генерируемого проекта. Она не означает, что выбран VS2013 toolset. Фактический результат проверяется по XML `.vcxproj`: CSS93 проекты содержат `PlatformToolset` `v143`, то есть используется toolset Visual Studio 2022.

## `/FORCE` и `_hypot`

В логе Client (CSS93) есть ожидаемые предупреждения:

```text
libucrt.lib(hypot.obj) : warning LNK4006: _hypot already defined in particles.lib(particle_sort.obj); second definition ignored
client.dll : warning LNK4088: image being generated due to /FORCE option; image may not run
```

В `mp/src/game/client/client_base.vpc` для `[$VS2022]` явно задан `/FORCE:MULTIPLE`. Он нужен из-за дублирования `_hypot` между `particles.lib` и VS2022 UCRT (`libucrt.lib`). Линковка завершилась успешно, а DLL были опубликованы; предупреждение LNK4088 следует считать известным ограничением текущей VS2022-сборки, а не доказательством сбоя CSS93.
