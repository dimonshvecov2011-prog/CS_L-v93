# Найденные проблемы и ограничения

- Среда разработки — Linux sandbox без Visual Studio 2022, Source SDK Base 2013 Multiplayer и Windows dedicated server. Реальная сборка DLL подтверждена через GitHub Actions; запуск карты и подключение клиента локально ещё не выполнены.
- База проекта — `Source-SDK-Resources/source-sdk-vs2022`; в ней подтверждены каталоги `mp/src`, `mp/game` и `mp/src/createallprojects.bat`.
- `createallprojects.bat` вызывает legacy VPC с `/mksln`, но этот режим не работает на runner с VS2022 из-за отсутствующей legacy registry-записи: `ERROR: Unable to find RegKey for .vcproj or .vcxproj files in solutions.` CI обходит ограничение прямой сборкой сгенерированных `.vcxproj`.
- `mp/game/css93/gameinfo.txt` использует публичный HL2MP-контент и AppID 243750. Собственные VPK, материалы, звуки и модели ещё не добавлены.
- CSS93-проекты зарегистрированы в VPC: `Client (CSS93)` и `Server (CSS93)`. Успешный run [#45151530011](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45151530011) подтвердил сборку `Release|Win32` и публикацию `client.dll`/`server.dll` в `mp/game/css93/bin`.
- Проверка Debug, локальная Windows-сборка, запуск через Steam, загрузка карты и запуск dedicated server пока не выполнены.
- В Client (HL2MP) и Client (CSS93) сохраняется предупреждение LNK4006 о дублировании `_hypot` между `particles.lib` и VS2022 `libucrt.lib`. Для VS2022 в `client_base.vpc` включён `/FORCE:MULTIPLE`; линковка завершается успешно, но остаётся предупреждение LNK4088 о `/FORCE`.
- Надпись VPC `Generating for Visual Studio 2013` относится к legacy-формату генератора. Сгенерированные CSS93 `.vcxproj` содержат `<PlatformToolset>v143</PlatformToolset>`, поэтому `/define:VS2022` применяется.
- Закрытые исходники и ассеты CS:S в этот репозиторий не добавляются.

## Лицензия

Исходники SDK распространяются по `LICENSE` Valve и сопровождаются `thirdpartylegalnotices.txt`. Распространение SDK или существенной его части должно быть бесплатным и включать эти файлы, копирайт и обязательные оговорки.
