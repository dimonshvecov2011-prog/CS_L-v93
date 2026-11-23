# Найденные проблемы и ограничения

Среда разработки — Linux sandbox без Visual Studio 2022 и Windows runtime, поэтому запуск проверяется на пользовательском Windows-ПК. Пользователь подтвердил: мод запускается, карты HL2DM видны, `dm_lockdown` загружается, сервер принимает подключение; после `Initializing renderer` клиент показывает чёрный экран и падает через секунду. Три дампа имеют `EXCEPTION_ACCESS_VIOLATION`, запись по адресу `0`, модуль `engine.dll`, смещение `0x15da10`. `client.dll` и `server.dll` CSS93 при этом загружаются.

В консоли во время сбоя не находились базовые ресурсы `vgui/gfx/vgui/crosshair`, `materials/vgui/logos/spray.vtf`, `sprites/redglow1` и `models/props_lab/monitor_lost`. Предыдущий `gameinfo.txt` был составлен неверно: он ссылался на `|appid_243750|hl2mp/hl2mp_english.vpk` и несуществующие для этой установки `hl2_complete_*`. Он заменён на шаблон, близкий к официальному `mp/game/mod_hl2mp/gameinfo.txt`: HL2DM подключается относительными путями `hl2mp/hl2mp_*.vpk`, общий HL2-контент — через `|all_source_engine_paths|hl2/...`, а loose-file roots подключены для `hl2mp`, `hl2` и `platform`. Владелец установки должен иметь установленный **Half-Life 2: Deathmatch** и необходимые базовые игры Source; пути нельзя привязывать к конкретной букве диска.

Для диагностического разреза подготовлены штатные HL2MP DLL из того же CI-run. Нужно сохранить текущие CSS93 DLL, заменить только `css93/bin/client.dll` и `css93/bin/server.dll` на штатные HL2MP-файлы и повторить запуск с теми же `gameinfo.txt`, картой и параметрами. Если падение останется, первичная причина, вероятно, в SearchPaths/базовом контенте или движке. Если исчезнет, виноваты CSS93 DLL и следующим шагом будет Debug-сборка CSS93 без `/FORCE:MULTIPLE` с PDB.

База проекта — `Source-SDK-Resources/source-sdk-vs2022`; подтверждены каталоги `mp/src`, `mp/game` и `mp/src/createallprojects.bat`. Legacy VPC `/mksln` не работает на runner с VS2022 из-за отсутствующей registry-записи, поэтому CI обходит ограничение прямой сборкой сгенерированных `.vcxproj`.

CSS93-проекты зарегистрированы в VPC: `Client (CSS93)` и `Server (CSS93)`. Run [#45151530011](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45151530011) подтвердил сборку `Release|Win32` и публикацию DLL в `mp/game/css93/bin`. Run [#45161557652](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45161557652) дополнительно опубликовал штатные HL2MP DLL и Release PDB для HL2MP/CSS93.

В Client (HL2MP) и Client (CSS93) сохраняется предупреждение LNK4006 о дублировании `_hypot` между `particles.lib` и VS2022 `libucrt.lib`. Для VS2022 в `client_base.vpc` включён `/FORCE:MULTIPLE`; линковка завершается успешно, но остаётся предупреждение LNK4088 о `/FORCE`. До A/B-теста этот workaround не изменяется: удалять `/FORCE` вслепую нельзя, поскольку сначала нужно отделить проблему SearchPaths от проблемы DLL.

Надпись VPC `Generating for Visual Studio 2013` относится к legacy-формату генератора. Сгенерированные CSS93 `.vcxproj` содержат `<PlatformToolset>v143</PlatformToolset>`, поэтому `/define:VS2022` применяется. Закрытые исходники и ассеты CS:S в этот репозиторий не добавляются.

## Лицензия

Исходники SDK распространяются по `LICENSE` Valve и сопровождаются `thirdpartylegalnotices.txt`. Распространение SDK или существенной его части должно быть бесплатным и включать эти файлы, копирайт и обязательные оговорки.
