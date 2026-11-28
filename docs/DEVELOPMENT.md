# Справочник разработчика

## Карта кода

Файлы интерфейса лежат в `src/UI/`, интеграция с Windows — в `src/Platform/`. В последнем столбце указаны точки входа для поиска по имени.

| Что меняется | Где начать | Что искать |
| --- | --- | --- |
| Запуск, один экземпляр на профиль, команды `--toggle` и `--exit` | [Program.cs](../src/Program.cs) | `Main`, `Run`. Здесь связываются хранилище, `PlatformServices` и `DesktopUi`. |
| Закрепления, перетаскивание, отрисовка Dock | [DockSurface.cs](../src/UI/DockSurface.cs), [DesktopUi.cs](../src/UI/DesktopUi.cs), [DockItem.cs](../src/DockItem.cs) | `DockPins` восстанавливает приложения, папки и разделители в порядке закрепления. `DockSurface.Render` размещает их на панели. |
| Конструктор значков | [IconEditor.cs](../src/UI/IconEditor.cs), [DesktopIcons.cs](../src/UI/DesktopIcons.cs), [IconDesign.cs](../src/IconDesign.cs) | `IconFor` связывает значок с приложением или папкой. `IconDesign.Render` рисует один и тот же вариант для редактора, Пуска и Dock. Импортированное изображение хранится в профиле. |
| Верхняя строка и область уведомлений | [StatusBarWindow.cs](../src/UI/StatusBarWindow.cs), [StatusBarPosition.cs](../src/Platform/StatusBarPosition.cs), [NotificationArea.cs](../src/Platform/NotificationArea.cs) | `DesktopUi.SyncScreens` создаёт строку на выбранных мониторах. В обычном режиме она резервирует место через Windows AppBar. При замене панели [TrayHost.cs](../src/Platform/TrayHost.cs) получает регистрации приложений и передаёт их Explorer. В совмещённом и пробном режимах он работает, пока раскрыта полоса значков. `NotificationArea` копирует HICON из регистрации и доставляет приложению щелчки. |
| Оформление меню верхней строки | [StatusMenuStyles.xaml](../src/UI/StatusMenuStyles.xaml), [StatusMenuChrome.cs](../src/UI/StatusMenuChrome.cs), [StatusMenus.cs](../src/UI/StatusMenus.cs) | Встроенный словарь XAML задаёт шаблоны бегунка, переключателей и строк выбора. Окно применяет цвета темы и анимацию появления через `Show`. |
| Меню звука | [StatusMenus.Sound.cs](../src/UI/StatusMenus.Sound.cs), [AudioEndpoints.cs](../src/Platform/AudioEndpoints.cs) | Core Audio перечисляет устройства воспроизведения и записи. Уровень и mute относятся к показанному устройству. Выбор меняет устройства по умолчанию для консоли и мультимедиа. |
| Меню сети и раскладки | [StatusMenus.cs](../src/UI/StatusMenus.cs), [SystemRadios.cs](../src/Platform/SystemRadios.cs), [WirelessNetworks.cs](../src/Platform/WirelessNetworks.cs), [KeyboardTarget.cs](../src/Platform/KeyboardTarget.cs) | Windows.Devices.Radios управляет Bluetooth и Wi-Fi, системный диспетчер радиосвязи — авиарежимом. Native Wi-Fi обслуживает подключения. Меню раскладки переключает поток ввода активного приложения. |
| Появление значков и перенос из Проводника | [DockItemMotion.cs](../src/UI/DockItemMotion.cs), [ShortcutPins.cs](../src/Platform/ShortcutPins.cs) | `DockItemMotion` анимирует изменения списка. `ResolveDrop` обрабатывает EXE, папки и ярлыки, включая приложения Microsoft Store. `DropFiles` сохраняет закрепления в порядке вставки. |
| Автоскрытие и несколько мониторов | [DesktopUi.cs](../src/UI/DesktopUi.cs), [WindowList.cs](../src/Platform/WindowList.cs), [ScreenInfo.cs](../src/Platform/ScreenInfo.cs) | `CoveredScreens` определяет перекрытые экраны, `UpdateVisibility` решает, когда показать Dock, `SetRevealed` анимирует его. |
| Пуск, поиск, страницы, клавиатура | [LaunchSurface.cs](../src/UI/LaunchSurface.cs), [LaunchGrid.cs](../src/UI/LaunchGrid.cs) | `RefreshCatalog`, `RenderPage`, `OnKey`. Переходы — в `LaunchNavigationMotion.cs`, фон — в `LaunchGlass.cs`. |
| Каталог Windows и папки | [ShellCatalog.cs](../src/Platform/ShellCatalog.cs), [StartFolders.cs](../src/Platform/StartFolders.cs), [LaunchLibrary.cs](../src/LaunchLibrary.cs) | Первые два файла читают приложения, значки и ярлыки Windows. `LaunchLibrary` хранит пользовательский порядок и размещение поверх каталога. Команды интерфейса — в `LaunchFolders.cs` и `LaunchFolderEditor.cs`. |
| Настройки, темы, шрифты и значки | [Preferences.xaml](../src/UI/Preferences.xaml), [Preferences.cs](../src/UI/Preferences.cs), [Appearance.cs](../src/UI/Appearance.cs) | `PreferencesColors.cs` — редактор палитры, `ThemeTransition.cs` — смена темы, `UiFonts.cs` — выбор установленного шрифта, `Visuals.Icon` — оформление значков. |
| Язык установщика и интерфейса | [Lunaris.iss](../packaging/Lunaris.iss), [Localization.cs](../src/Localization.cs), [каталоги переводов](../src/Languages/) | Установщик записывает `InstallLanguage` в `HKCU\Software\LunarisDock`. Значение `Language` в профиле имеет приоритет. Новые строки добавляйте с одинаковым ключом и параметрами формата во все пять каталогов. |
| Liquid Glass | [LiquidGlassMaterial.cs](../src/UI/LiquidGlassMaterial.cs), [LaunchGlass.cs](../src/UI/LaunchGlass.cs) | `LiquidGlass` хранится отдельно от цветовой темы. Кисти используются в Dock, Пуске и настройках, а Пуск также преломляет размытый образец фона у кромки окна. |
| Уведомления о релизах | [ReleaseUpdates.cs](../src/ReleaseUpdates.cs), [Preferences.cs](../src/UI/Preferences.cs) | `LatestAsync` проверяет опубликованный релиз на GitHub при запуске и затем каждые шесть часов. `ShowUpdateBadge` управляет меткой на кнопке «Пуск». Ссылка в настройках открывает страницу соответствующего тега. |
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
| Редактор значков, верхняя строка и действия трея | `.\tests\Preview.Tests.ps1 -ExePath .\build\LunarisDock.exe -AppearanceOnly` запускает пробную копию с отдельным профилем и тестовым значком в трее. |
| Устройства и увеличение значков Dock | `.\tests\Preview.Tests.ps1 -ExePath .\build\LunarisDock.exe -DevicesOnly` сверяет меню с состоянием Windows и проверяет края увеличенных значков на экранах. Ключ `-ExerciseAudio` переключает устройства и проверяет восстановление уровней, mute и системных ролей. Ключ `-ExerciseRadios` проверяет переключение Bluetooth и авиарежима на стенде с Ethernet и свободным Bluetooth-адаптером. |
| Верхняя строка при полной замене панели Windows | Включите «Заменять панель задач», затем выполните `.\tests\LiveStatusBar.Tests.ps1 -DockProcessId <PID основного процесса> -ReferenceExe <путь к его EXE>`. Для двух масштабов выберите все экраны в профиле Dock и добавьте `-RequireMixedDpi`. Проверка наблюдает за событиями показа окон Explorer, сверяет рисунок тестового приложения до и после смены значка, проверяет щелчки, выбор команды, меню строки, центр уведомлений, обе точки входа в Пуск. Сетевой сценарий сохраняет подключение. Отдельный сценарий можно выбрать через `-Scenario`. |
| Разворачивание окон при замене панели | `LiveStatusBar.Tests.ps1 -DockProcessId <PID> -ReferenceExe <EXE> -Scenario WorkArea` разворачивает тестовое окно на каждом экране и сверяет его видимую рамку с рабочей областью Windows и нижним краем экрана. Для этого сценария освободите нижний край от сторонних панелей. Его можно запускать и с выключенной верхней строкой Lunaris. |
| Папки и сортировка Пуска | Та же команда Preview с `-LibraryOnly`. Обе проверки управляют мышью и окнами текущего рабочего стола. |
| Состав portable ZIP и пути профилей | `.\build.ps1 -PortableOnly`, затем `.\tests\Portable.Tests.ps1 -PackageOnly`. |
| Повторная упаковка и сохранность данных | `.\tests\Portable.Publish.Tests.ps1` проверяет публикацию и откат во временном каталоге. |

Полный прогон из ZIP, проверка на двух экранах и расположение журналов описаны в [приёмке portable](../tests/Проверка%20переносимой%20сборки.md). Проверки установки, удаления, перезапуска Explorer и блокировки экрана выполняются в тестовой учётной записи или ВМ по [инструкции для оболочки](../tests/Проверка%20оболочки.md).

## Публикация

1. Версия приложения указана в [AssemblyInfo.cs](../src/AssemblyInfo.cs), версия пакета — в [build.ps1](../build.ps1). При смене версии найдите прежний номер через `git grep -n -F` с номером как аргументом. Он также используется в настройках сборки установщика, путях тестов и ссылках README.
2. Для выпуска выполните `.\build.ps1` из чистой копии коммита. Установщик описан в [Lunaris.iss](../packaging/Lunaris.iss), ZIP формирует [Publish-Portable.ps1](../scripts/Publish-Portable.ps1) по списку файлов. Скрипты кладут результат в `release/<версия>/`. В GitHub Releases к тегу этого коммита прикладываются `*-Setup.exe`, `*-Portable.zip` и оба файла `.sha256`. В README и описании релиза первой идёт ссылка на установщик.
3. В Git добавляйте нужные файлы по именам. [.gitignore](../.gitignore) разрешает конкретные пути. Если новый документ скрыт, проверьте его через `git check-ignore -v` и добавьте исключение для этого файла. Перед отправкой проверьте `git diff --cached`, список файлов и данные автора и коммитера (`git var GIT_AUTHOR_IDENT`, `git var GIT_COMMITTER_IDENT`). Для публичного коммита можно использовать адрес GitHub noreply или подпись проекта из истории. Каталоги `build/`, `release/`, профили и журналы остаются локально.
4. Для галереи README используйте демонстрационный профиль **и** нейтральный фон. Обычный `qa/Capture.ps1` может снять текущие обои и названия установленных приложений даже на отдельном рабочем столе. Перед публикацией просмотрите кадры и проверьте метаданные PNG. Публичные изображения лежат в [docs/screenshots](screenshots/).
