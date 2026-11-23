# Windows CI

Репозиторий уже создан как приватный: [dimonshvecov2011-prog/css93](https://github.com/dimonshvecov2011-prog/css93).

Workflow находится в `.github/workflows/windows-hl2mp.yml`. Он запускается на `windows-latest`, переходит в `mp/src` и напрямую вызывает `devtools\bin\vpc.exe /hl2mp +everything /define:VS2022`, без `/mksln`. Затем MSBuild отдельно собирает `client_hl2mp.vcxproj` и `server_hl2mp.vcxproj` в конфигурации `Release|Win32`. Логи генерации и сборки сохраняются как GitHub Actions artifact.

## Текущий результат

Первый run [#45149824180](https://github.com/dimonshvecov2011-prog/css93/actions/runs/45149824180) завершился с ошибкой на генерации solution. VPC успешно создал `.vcxproj`, но старый бинарник VPC завершился с `ERROR: Unable to find RegKey for .vcproj or .vcxproj files in solutions.` Поэтому сборка DLL не началась. Новый вариант не создаёт `.sln` и проверяет два штатных проекта напрямую.

У Manus нет собственного GitHub-аккаунта или логина, поэтому принять collaborator-приглашение и выполнить push от имени Manus невозможно. Для публикации нужен push владельцем репозитория либо подключённый GitHub connector с правом записи.
