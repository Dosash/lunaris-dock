# Справочник разработчика

## Карта кода

Файлы интерфейса лежат в `src/UI/`, интеграция с Windows — в `src/Platform/`. В последнем столбце указаны точки входа для поиска по имени.

| Что меняется | Где начать | Что искать |
| --- | --- | --- |
| Запуск, один экземпляр на профиль, команды `--toggle` и `--exit` | [Program.cs](../src/Program.cs) | `Main`, `Run`. Здесь связываются хранилище, `PlatformServices` и `DesktopUi`. |
| Закрепления, перетаскивание, отрисовка Dock | [DockSurface.cs](../src/UI/DockSurface.cs), [DesktopUi.cs](../src/UI/DesktopUi.cs) | `DockSurface.Render` рисует кнопки, `DesktopUi` обновляет порядок закреплений. |
| Автоскрытие и несколько мониторов | [DesktopUi.cs](../src/UI/DesktopUi.cs), [WindowList.cs](../src/Platform/WindowList.cs), [ScreenInfo.cs](../src/Platform/ScreenInfo.cs) | `CoveredScreens` определяет перекрытые экраны, `UpdateVisibility` решает, когда показать Dock, `SetRevealed` анимирует его. |
| Пуск, поиск, страницы, клавиатура | [LaunchSurface.cs](../src/UI/LaunchSurface.cs), [LaunchGrid.cs](../src/UI/LaunchGrid.cs) | `RefreshCatalog`, `RenderPage`, `OnKey`. Переходы — в `LaunchNavigationMotion.cs`, фон — в `LaunchGlass.cs`. |
| Каталог Windows и папки | [ShellCatalog.cs](../src/Platform/ShellCatalog.cs), [StartFolders.cs](../src/Platform/StartFolders.cs), [LaunchLibrary.cs](../src/LaunchLibrary.cs) | Первые два файла читают приложения, значки и ярлыки Windows. `LaunchLibrary` хранит пользовательский порядок и размещение поверх каталога. Команды интерфейса — в `LaunchFolders.cs` и `LaunchFolderEditor.cs`. |
| Настройки, темы, шрифты и значки | [Preferences.xaml](../src/UI/Preferences.xaml), [Preferences.cs](../src/UI/Preferences.cs), [Appearance.cs](../src/UI/Appearance.cs) | `PreferencesColors.cs` — редактор палитры, `ThemeTransition.cs` — смена темы, `UiFonts.cs` — выбор установленного шрифта, `Visuals.Icon` — оформление значков. |
| Клавиша Win, штатный Пуск, замена панели задач | [PlatformServices.cs](../src/Platform/PlatformServices.cs) | `WinKey.cs`, `WindowsStartRedirect.cs`, `TaskbarGuard.cs`. Последний также восстанавливает состояние панели после сбоя. |

## Профили и настройки

В [Models.cs](../src/Models.cs) лежат `UserSettings` и `SettingsStore`. При добавлении поля проверьте начальное значение для нового профиля и чтение старого JSON в `Reading` и `Normalize`. Хранилище сохраняет предыдущую версию в `settings.json.bak`. Переключатели вызывают `DesktopUi.Changed()`. Ползунок размера использует `ResizeDock()`, чтобы обновлять Dock во время движения и записывать настройку с задержкой.

| Запуск | Расположение профиля |
| --- | --- |
| Установленная версия | `%LOCALAPPDATA%\LunarisDock\Profile`, в пробном режиме — соседний каталог `Preview` |
| Портативная версия | `Data\Profile` или `Data\Preview` рядом с EXE |
| Задана переменная `LUNARIS_PROFILE` | Указанный абсолютный каталог имеет приоритет в обоих режимах |

Для ручной проверки после `-AppOnly` откройте отдельный PowerShell в корне проекта. Пробный режим оставляет клавишу Win и панель задач под управлением Windows.

```powershell
$env:LUNARIS_PROFILE = Join-Path $PWD 'build\demo-profile'
.\build\LunarisDock.exe --preview
```

## Проверки

| Изменение | Команда из корня |
| --- | --- |
| Модели, каталог, отрисовка, анимации | После `-AppOnly` выполните `.\tests\Core.Tests.ps1 -ExePath .\build\LunarisDock.exe`. |
| Окно настроек | `.\tests\Preview.Tests.ps1 -ExePath .\build\LunarisDock.exe -SettingsOnly` |
| Папки и сортировка Пуска | Та же команда Preview с `-LibraryOnly`. Обе проверки управляют мышью и окнами текущего рабочего стола. |
| Состав portable ZIP и пути профилей | `.\build.ps1 -PortableOnly`, затем `.\tests\Portable.Tests.ps1 -PackageOnly`. |
| Повторная упаковка и сохранность данных | `.\tests\Portable.Publish.Tests.ps1` проверяет публикацию и откат во временном каталоге. |

Полный прогон из ZIP, проверка на двух экранах и расположение журналов описаны в [приёмке portable](../tests/Проверка%20переносимой%20сборки.md). Проверки установки, удаления, перезапуска Explorer и блокировки экрана выполняются в тестовой учётной записи или ВМ по [инструкции для оболочки](../tests/Проверка%20оболочки.md).

## Публикация

1. Версия приложения указана в [AssemblyInfo.cs](../src/AssemblyInfo.cs), версия пакета — в [build.ps1](../build.ps1). При смене версии найдите прежний номер через `git grep -n -F` с номером как аргументом. Он также используется в настройках сборки установщика, путях тестов и ссылках README.
2. Для выпуска выполните `.\build.ps1` из чистой копии коммита. Установщик описан в [Lunaris.iss](../packaging/Lunaris.iss), ZIP формирует [Publish-Portable.ps1](../scripts/Publish-Portable.ps1) по списку файлов. Скрипты кладут результат в `release/<версия>/`. В GitHub Releases к тегу этого коммита прикладываются `*-Setup.exe`, `*-Portable.zip` и оба файла `.sha256`. В README и описании релиза первой идёт ссылка на установщик.
3. В Git добавляйте нужные файлы по именам. [.gitignore](../.gitignore) разрешает конкретные пути. Если новый документ скрыт, проверьте его через `git check-ignore -v` и добавьте исключение для этого файла. Перед отправкой проверьте `git diff --cached`, список файлов и данные автора и коммитера (`git var GIT_AUTHOR_IDENT`, `git var GIT_COMMITTER_IDENT`). Для публичного коммита можно использовать адрес GitHub noreply или подпись проекта из истории. Каталоги `build/`, `release/`, профили и журналы остаются локально.
4. Для галереи README используйте демонстрационный профиль **и** нейтральный фон. Обычный `qa/Capture.ps1` может снять текущие обои и названия установленных приложений даже на отдельном рабочем столе. Перед публикацией просмотрите кадры и проверьте метаданные PNG. Публичные изображения лежат в [docs/screenshots](screenshots/).
