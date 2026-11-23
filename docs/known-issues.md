# Найденные проблемы и ограничения

- GitHub CLI в текущей сессии не авторизован, поэтому приватный удалённый репозиторий пока не создан. Локальная копия подготовлена отдельно.
- Среда разработки — Linux sandbox без Visual Studio 2022, Source SDK Base 2013 Multiplayer и Windows dedicated server. Реальная сборка DLL, запуск карты и подключение клиента требуют Windows.
- База проекта переключена на `Source-SDK-Resources/source-sdk-vs2022`: в ней подтверждены каталоги `mp/src`, `mp/game` и `mp/src/createallprojects.bat`.
- `createallprojects.bat` вызывает `devtools\bin\vpc.exe /hl2mp +everything /mksln everything.sln /define:VS2022`; обязательной зависимости от Python не найдено.
- `mp/game/css93/gameinfo.txt` пока использует публичный HL2MP-контент и AppID 243750. Собственные DLL, VPK, материалы, звуки и модели ещё не добавлены.
- Проекты `Client (CSS93)` и `Server (CSS93)` ещё не зарегистрированы в VPC-генерации.
- Штатная сборка `Client (HL2MP)` и `Server (HL2MP)` в Debug/Release Win32 пока не проверена.
- Выход DLL в `mp/game/css93/bin`, запуск через Steam и запуск dedicated server пока не проверены.
- Первый GitHub Actions run `45149824180` успешно прошёл checkout и настройку MSBuild, но VPC упал на `/mksln`: `ERROR: Unable to find RegKey for .vcproj or .vcxproj files in solutions.` Лог сохранён в artifact `windows-hl2mp-build-logs`. Это ограничение старого VPC при генерации solution на runner с Visual Studio 2022; CI переведён на прямую сборку двух `.vcxproj` без `/mksln`.
- До появления DLL папка `mp/game/css93` не является запускаемым модом.

## Лицензия

Исходники SDK распространяются по `LICENSE` Valve и сопровождаются `thirdpartylegalnotices.txt`. Распространение SDK или существенной его части должно быть бесплатным и включать эти файлы, копирайт и обязательные оговорки. Закрытые исходники и ассеты CS:S в этот репозиторий не добавляются.
