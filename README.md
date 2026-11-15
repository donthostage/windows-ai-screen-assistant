# Windows AI Screen Assistant

WPF-приложение для периодического анализа и длинных страниц через AI Tunnel.

## Сборка и запуск

Нужна Windows 10 версии 2004 (сборка 19041) или новее, включая Windows 11, и [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Для запуска framework-dependent сборки нужен **.NET Desktop Runtime 8**. На компьютере с SDK той же архитектуры он уже установлен. Windows Graphics Capture требует интерактивного рабочего стола и проверяется при открытии системного выбора.

В **Visual Studio 2022 17.8+** установите рабочую нагрузку «Разработка классических приложений .NET», откройте `ScreenAssistant.sln`, выберите `ScreenAssistant` запускаемым проектом и нажмите F5. В VS Code откройте папку проекта и выполните в PowerShell:

```powershell
dotnet restore .\ScreenAssistant.sln
dotnet build .\ScreenAssistant.sln -c Release --no-restore
dotnet test .\ScreenAssistant.sln -c Release --no-build
dotnet run --project .\src\ScreenAssistant\ScreenAssistant.csproj -c Release --no-build
```

Автономная сборка для переноса на Windows x64 включает .NET Desktop Runtime:

```powershell
dotnet publish .\src\ScreenAssistant\ScreenAssistant.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64-panel
```

Запустите `ScreenAssistant.exe` из папки публикации. Для Windows на ARM укажите `win-arm64`. Папку публикации переносите целиком.

## Первый запуск

Введите Base URL, ключ API и ID модели. Каталог AI Tunnel показывает модели с поддержкой изображений для режима длинной страницы.

Хоткеи: **Ctrl+Alt+L** — длинная страница, **Ctrl+Alt+H** — оверлей, **Ctrl+Alt+M** — главное окно, **Ctrl+Alt+J/K** — прокрутка многострочного оверлея.

## Периодический анализ

Выберите источник, задайте интервал от 5 до 3600 секунд и нажмите **Запустить анализ**. Первый кадр снимается сразу. Одинаковые кадры пропускаются. Хоткей **Ctrl+Alt+L** отменяет текущий запрос и запускает серию. После ответа по серии периодический анализ возобновляется через полный интервал. **Остановить** отменяет текущую операцию и выключает периодический режим.

## Длинная страница

Выберите источник захвата. Перейдите к началу тренировочной страницы и нажмите Ctrl+Alt+L. После трёхсекундного отсчёта плавно прокручивайте страницу с перекрытием между кадрами. Приложение делает до восьми снимков с паузой около двух секунд, отбрасывает точные дубликаты и отправляет оставшиеся кадры одним запросом. Ответ открывается в главном окне. При ошибке это же окно показывает причину. Если анализ уже идёт, Ctrl+Alt+L отменяет его и запускает новый сбор после освобождения захвата.

## Плавающее окно

В многострочном режиме **Ctrl+Alt+K** прокручивает ответ вверх на одну страницу, **Ctrl+Alt+J** — вниз. Чтобы включить многострочный режим, снимите галочку «Компактная строка».

- Включите **Плавающий ответ** в верхней панели. Окно по умолчанию внизу по центру рабочей области (отступ 40 px), ширина около 60% рабочей области, высота 180. Компактный режим показывает первую строку ответа; полный текст — во вкладке **Последний ответ**.
- Во вкладке **Оверлей**: режим, размеры, шрифт, непрозрачность, **«Скрывать оверлей из записи экрана»** (по умолчанию включено, `SetWindowDisplayAffinity` / WDA_EXCLUDEFROMCAPTURE на Windows 10 2004+), **«Плотный фон»** (без прозрачности; применяется при следующем открытии окна, если layered-окно плохо исключается на GPU). Оверлей оформлен как tool-window: не появляется в панели задач и Alt+Tab.
- Если исключение из захвата не удалось, статус предупредит — включите плотный фон, перенесите окно или отключите переключатель скрытия. Остановка тренировки оставляет последний полный ответ доступным для чтения.
- Положение и вид сохраняются в `%LOCALAPPDATA%\ScreenAssistant\overlay.json`.

## Настройки и данные

Образец — [settings.example.json](settings.example.json). Его можно скопировать в `%LOCALAPPDATA%\ScreenAssistant\settings.json` перед запуском или задать параметры через интерфейс.

- `settings.json` хранит Base URL и ID модели.
- Файлы `key-*.dpapi` содержат зашифрованные ключи текущего пользователя Windows. Приложение подставляет ключ для текущего Base URL и отключает HTTP-перенаправления.
- `errors.log` содержит время, этап, тип ошибки, HRESULT и HTTP-статус, если он доступен. Сохраняется один предыдущий журнал. Тела запросов и ответов, ключи и изображения в журнал не записываются.
- В оперативной памяти остаются текущий предпросмотр и до 200 ответов. История сбрасывается при начале новой тренировки после успешной проверки модели.

## Структура

```text
ScreenAssistant.sln
settings.example.json
src/
  ScreenAssistant/
    ScreenAssistant.csproj
    app.manifest
    App.xaml, App.xaml.cs
    MainWindow.xaml, MainWindow.xaml.cs
    Overlay/OverlayWindow.xaml, OverlayWindow.xaml.cs, OverlayViewModel.cs
    Overlay/OverlayPreferences.cs, WpfOverlayRenderer.cs, CaptureExclusion.cs
    Overlay/CropSelectionWindow.xaml(.cs)
    Hotkeys/HotkeyManager.cs
    Configuration/UserSettings.cs
    ScreenCapture/D3DDevice.cs, CaptureSession.cs, GraphicsCapture.cs, FrameEncoding.cs
  ScreenAssistant.Core/
    ScreenAssistant.Core.csproj
    AIClient/AiClient.cs, ResponseParser.cs
    Geometry/FrameGeometry.cs
    Overlay/IOverlayService.cs, OverlayManager.cs
    Scheduler/AnalysisScheduler.cs
tests/
  ScreenAssistant.Tests/
    ScreenAssistant.Tests.csproj
    AiClientTests.cs, AnalysisSchedulerTests.cs, OverlayManagerTests.cs, FrameGeometryTests.cs
```

- **CaptureSession** открывается на время снимка и освобождает ресурсы после него. Между снимками захват закрыт. Курсор в кадр не входит.
- **FrameEncoding** кропает область (если задана) до масштабирования, длинная сторона ≤ 2560 от размеров кропа; PNG до 8 МиБ, иначе JPEG q=95. Fingerprint — SHA-256 PNG после кропа/масштаба.
- **AIClient** повторно использует один `HttpClient` и отправляет `image_url` с PNG/JPEG data URI в `chat/completions`. Тайм-аут API — 60 секунд. Для модели требуется поддержка изображения на входе и обычного текстового ответа в `choices[0].message.content`. Если конкретный ID, пресет или формат ответа несовместим, выберите модель из каталога и повторите запуск.
- **Scheduler** отсчитывает интервал между началами анализов, ожидает завершения текущей операции и отменяет её при остановке или истечении времени. Автоматических повторов внутри запроса нет. При временной ошибке следующий цикл учитывает `Retry-After`, а при HTTP 429 без этого заголовка действует пауза от 30 секунд. Ошибки параметров, ключа, доступа и оплаты завершают тренировку.
- **OverlayManager** получает статусы и части ответа через `IOverlayService`. `ResponseParser` читает SSE, а менеджер принимает обновления только для активного запроса и передаёт их в WPF через Dispatcher. `OverlayViewModel` связывает текст, статус и настройки с окном. Компоненты создаются и связываются в `App.OnStartup`.
- **Тесты** используют подменный HTTP-обработчик и управляемые часы. Реальный ключ для них не нужен. CI на GitHub Actions (`windows-latest`) собирает solution и запускает `dotnet test`. Для ручной проверки Windows выберите монитор, получите локальный кадр, затем запустите тренировку с действующим ключом. Проверьте ответ, историю, остановку и повторный выбор источника.

## Проверка оверлея

Для ручной проверки оверлея включите **Плавающий ответ**, запустите тренировку и убедитесь, что текст появляется до окончания генерации. Закройте и снова откройте окно во время ответа. Проверьте перенос на другой монитор, изменение вида и восстановление положения после перезапуска. Убедитесь, что оверлей не виден на предпросмотре кадра того же монитора (или что статус предупредил об отсутствии исключения). После остановки последующий фрагмент отменённого запроса не должен менять ответ.

## Документация

- [Windows Graphics Capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
- [AI Tunnel — запросы с изображениями](https://aitunnel.ru/docs/images)
- [AI Tunnel — каталог моделей](https://aitunnel.ru/docs/models)
- [AI Tunnel — потоковые ответы](https://aitunnel.ru/docs/streaming)
