# CS:S Steam Launcher — Stage 1

Каркас Windows-лаунчера для текущей Steam-сборки Counter-Strike: Source. Совместимость определяется по версии клиента из `cstrike/steam.inf`, а Steam `buildid` используется как дополнительная диагностическая информация. Файлы Valve в репозитории отсутствуют.

## Подтверждённые значения текущей копии

В предоставленных файлах обнаружено:

```text
steam.inf:
PatchVersion=11003710
ClientVersion=11003710
ServerVersion=11003710
ProductName=cstrike
appID=240
ServerAppID=232330

appmanifest_240.acf:
buildid=25338318
TargetBuildID=25338318
```

`11003710` и `25338318` — разные идентификаторы. Поэтому лаунчер сравнивает `ClientVersion`/`PatchVersion` с `knownVersions`, а не с `knownBuilds`.

## Сборка

Требуется Windows 10/11 и .NET 8 SDK:

```powershell
dotnet restore
dotnet build CssLauncher.sln -c Release
dotnet test tests/CssLauncher.Tests/CssLauncher.Tests.csproj
```

Запускаемый проект — `src/CssLauncher.App/CssLauncher.App.csproj`. WPF-приложение предназначено для Windows.

## Манифест совместимых версий

`config/version-manifest.v93.json`:

```json
{
  "schemaVersion": 2,
  "game": "Counter-Strike Source",
  "version": "11003710",
  "knownVersions": [11003710],
  "knownBuilds": [25338318],
  "enableSha256Checks": false,
  "variants": []
}
```

`knownVersions` — список допустимых значений `ClientVersion`/`PatchVersion` из `steam.inf`. Если значение есть в списке, статус — `Confirmed`. Если список пуст, статус — `ManifestEmpty`. Если `steam.inf` найден, но версия отсутствует в списке, статус — `UnknownBuild`; запуск разрешается только после включения диагностического режима или явного подтверждения в интерфейсе.

`knownBuilds` хранит ожидаемые Steam `buildid` только для информации и будущих проверок обновлений. Он не сравнивается с `ClientVersion`.

SHA-256 оставлена как дополнительная проверка. Чтобы включить её, установите `enableSha256Checks` в `true` и добавьте заполненные варианты в `variants`. По умолчанию SHA-256 выключена, потому что Steam-файлы могут изменяться при обновлениях.

## Как узнать идентификаторы

В консоли игры выполните:

```text
version
```

Сохраните `Build Label` и `Client/Server version`. Основной номер для лаунчера берётся из:

```text
<GameRoot>\cstrike\steam.inf
```

Нужны строки `ClientVersion` и `PatchVersion`. Steam-номер берётся из:

```text
<SteamLibrary>\steamapps\appmanifest_240.acf
```

Нужна строка `buildid`. Если Steam обновил игру, добавьте новый номер `ClientVersion`/`PatchVersion` в `knownVersions`, а соответствующий Steam `buildid` запишите в `knownBuilds`.

## Исполняемый файл и Steam

Лаунчер ищет `cstrike.exe`, затем `hl2.exe`, пишет оба найденных пути в лог и позволяет выбрать exe в интерфейсе. По умолчанию используется `cstrike.exe`. В запуск добавляется `-steam`, если установлен соответствующий флажок; он включён по умолчанию. Перед запуском проверяется наличие процесса Steam.

## Ручная проверка профиля

Подробная пошаговая инструкция с проверкой обоих exe, команд `path` и `version`, а также загрузки из `custom/` находится в [docs/manual-check-game-launch.md](docs/manual-check-game-launch.md).

Профили CS:L создаются в `%AppData%\CssLauncher\profiles\<id>\`. По умолчанию лаунчер автоматически предпочитает `cstrike.exe`, а при его отсутствии использует `hl2.exe`.

```bat
cstrike.exe -steam -game "%AppData%\CssLauncher\profiles\classic" -novid -console
```

Или:

```bat
hl2.exe -steam -game "%AppData%\CssLauncher\profiles\classic" -novid -console
```

Если потребуется изменить порядок или `custom/*`, изменяйте только `templates/gameinfo.template.txt`, а не код.

## Обновление игры

Если Steam обновил игру до новой `ClientVersion`, а встроенные расширения проверялись для версии `11003710`, лаунчер сообщает и пишет в лог:

> Установлена ClientVersion X, встроенные расширения проверены для версии 11003710. Возможна несовместимость.

Автоматической миграции или блокировки обновлений на этом этапе нет.

## Ограничения безопасности

Лаунчер не изменяет оригинальную папку игры, не распространяет файлы Valve и не требует администратора. Пути из манифеста проверяются как относительные. Аргументы передаются через `ProcessStartInfo.ArgumentList`, поэтому пробелы и кириллица в путях не требуют ручного экранирования.

Профиль и настройки находятся в `%AppData%\CssLauncher\`. Логи — в `%AppData%\CssLauncher\logs\`.


## Этап 2: CS:L

В приложении появился бренд **CS:L**, тёмная игровая оболочка, автоматический поиск библиотек Steam и кнопка **«Установить через Steam»**. Кнопка открывает официальный URI `steam://install/240`; саму игру, лицензию и файлы Valve загружает только Steam. После обнаружения каталога лаунчер повторно проверяет `steam.inf`, определяет `cstrike.exe`/`hl2.exe` и создаёт внешний профиль.

Обычному пользователю не нужны команды PowerShell: достаточно установить Steam, установить CS:S через кнопку в CS:L и нажать **«ИГРАТЬ»**. При необходимости можно выбрать папку вручную. CS:L не копирует и не изменяет оригинальные файлы игры.

Автоматически создаются:

```text
%AppData%\CssLauncher\profiles\classic\custom
%AppData%\CssLauncher\backups
%AppData%\CssLauncher\logs
```

## Встроенные плагины CS:L

CS:L не содержит каталога сторонних модификаций. Проект распространяет только собственные плагины и улучшения, которые включаются через версии лаунчера и профили `Classic`, `Modern` и `Competitive`. Ресурсы профиля изолированы в `%AppData%\CssLauncher\profiles\<id>\custom`; оригинальная папка Steam не изменяется.

## Сборка установщика Windows

Для self-contained публикации на Windows с установленным .NET 8 SDK выполните:

```powershell
.\installer\publish.ps1
```

Результат появится в `artifacts\publish\win-x64`. Чтобы собрать красивый установщик, установите [Inno Setup](https://jrsoftware.org/isinfo.php), затем выполните:

```powershell
iscc .\installer\CSL-Setup.iss
```

Готовый файл появится в `artifacts\installer`. В установщик попадает только приложение CS:L и документация; файлы Counter-Strike: Source, Steam и Valve туда не включаются.

## Автоматическая сборка через GitHub Actions

Файл `.github/workflows/build-csl.yml` собирает проект на `windows-latest` без установки .NET или Inno Setup на компьютере разработчика. Workflow выполняет restore, build, тесты, self-contained publish и собирает `CSL-Setup-0.3.1.exe`. Готовые portable-файлы и установщик доступны во вкладке **Actions → Artifacts**.

Для релизной сборки создайте и отправьте тег:

```powershell
git tag v0.3.1
git push origin v0.3.1
```

Для тега `v*` workflow дополнительно создаёт GitHub Release и прикладывает установщик. Сам репозиторий не должен содержать файлы CS:S, Steam или Valve.

## CS:L 0.3.1 — проверенный функционал

В версии 0.3.1 сохранены три профиля запуска: `Classic`, `Modern` и `Competitive`. Каждый профиль получает отдельную папку `profiles/<id>/custom`, поэтому переключение набора не изменяет оригинальные файлы CS:S.

Серверный список поддерживает локальный JSON и HTTP(S)-источник. В комплекте есть `config/servers.example.json` как демонстрационный формат. Профили управляют настройками запуска CS:L; каталог `custom` зарезервирован для собственных ресурсов проекта и не управляется как каталог сторонних модификаций.

Кнопка проверки обновлений читает `config/launcher-update.json`, сравнивает SemVer, а при настроенном HTTPS-канале скачивает архив в `%AppData%\\CssLauncher\\updates` во временный файл и проверяет размер и SHA-256 до замены. Диагностический архив собирается в `%AppData%\\CssLauncher\\backups` и включает настройки, логи, сведения о версии Steam и профиль без копирования файлов Valve.

Конфигурации 0.3.1 копируются в output автоматически: `servers.example.json` и `launcher-update.json`. Для сборки Windows нужен .NET 8 SDK; текущий Sandbox не содержит `dotnet`, поэтому здесь выполнены статические проверки XAML-событий и JSON, а не компиляция WPF.
