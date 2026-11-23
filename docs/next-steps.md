# Следующие шаги после документационного исправления

Сначала нужен Windows-runner: локальная Windows-машина, облачная Windows или GitHub Actions с `windows-latest` и Visual Studio 2022. На нём следует собрать штатные `Client (HL2MP)` и `Server (HL2MP)` в Debug и Release Win32 и записать фактический результат.

После успешной штатной сборки нужно создать VPC-файлы CSS93 на основе HL2MP, добавить их в `mp/src/vpc_scripts/projects.vgc` и `groups.vgc`, перегенерировать `everything.sln`, настроить выход в `mp/game/css93/bin` и проверить имена `client.dll` и `server.dll`.

Затем выполняется запуск через Steam с `map dm_lockdown`, запуск dedicated server и локальное подключение. Только после этой проверки можно считать фундамент завершённым и переходить к движению, оружию, раунду и HUD.

Создание приватного GitHub-репозитория и Windows CI будет выполнено после авторизации GitHub CLI или подключения GitHub-интеграции. До этого удалённая публикация невозможна.
