# Windows CI

Репозиторий уже создан как приватный: [dimonshvecov2011-prog/css93](https://github.com/dimonshvecov2011-prog/css93).

Workflow находится в `.github/workflows/windows-hl2mp.yml`. Он запускается на `windows-latest`, переходит в `mp/src` и напрямую вызывает `devtools\bin\vpc.exe /hl2mp +everything /mksln everything.sln /define:VS2022`, затем собирает штатное решение в конфигурации `Release|Win32`. Это решение включает проекты `Client (HL2MP)` и `Server (HL2MP)`. Логи генерации и сборки сохраняются как GitHub Actions artifact.

## Текущий результат

CI пока не запускался: репозиторий создан, но локальные коммиты ещё не отправлены в него. Поэтому результат сборки сейчас — **не проверено**. После первого push нужно записать сюда ссылку на run и итог `success` либо краткие ошибки из artifact.

У Manus нет собственного GitHub-аккаунта или логина, поэтому принять collaborator-приглашение и выполнить push от имени Manus невозможно. Для публикации нужен push владельцем репозитория либо подключённый GitHub connector с правом записи.
