# Windows CI

Workflow находится в `.github/workflows/windows-hl2mp.yml`. Он запускается на `windows-latest`, генерирует `mp/src/everything.sln` командой `createallprojects.bat`, затем собирает штатное решение в конфигурации `Release|Win32`. Это решение включает проекты `Client (HL2MP)` и `Server (HL2MP)`. Логи генерации и сборки сохраняются как GitHub Actions artifact.

## Текущий результат

CI пока не запускался: приватный GitHub-репозиторий ещё не создан и workflow не опубликован. Поэтому результат сборки сейчас — **не проверено**. После первой публикации нужно записать сюда ссылку на run и итог `success` либо краткие ошибки из artifact.
