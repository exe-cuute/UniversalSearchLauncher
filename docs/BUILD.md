# Сборка и упаковка

## Требования

- Windows 10/11 x64.
- .NET SDK 8.0 (для `dotnet publish`).
- .NET Framework 4.8 — для компиляции установщика и деинсталлятора через `csc.exe`.
- PowerShell 5.1+.
- Сетевой доступ NuGet либо локальный кэш runtime packs.

Для self-contained публикации нужны runtime-паки .NET 8 для `win-x64`:
`Microsoft.NETCore.App.Runtime.win-x64`, `Microsoft.WindowsDesktop.App.Runtime.win-x64` (и `Microsoft.AspNetCore.App.Runtime.win-x64`, который запрашивает SDK при публикации).

В этом репозитории предусмотрен локальный офлайн-режим: если существует `.tools\packages`, сборочный скрипт использует `scripts\NuGet.offline.config` и не обращается в интернет.

## Полная сборка

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

Скрипт:

1. восстанавливает пакеты;
2. запускает тесты;
3. публикует self-contained `win-x64` приложение в `artifacts\publish`;
4. компилирует `Uninstall.exe`;
5. упаковывает payload и компилирует `Setup.exe` (встраивает payload как ресурс);
6. создаёт портативный и исходный ZIP;
7. считает SHA-256.

Результат в `dist\`:

```text
UniversalSearchLauncher-Setup-1.0.1.exe       установщик
UniversalSearchLauncher-1.0.1-portable.zip    портативная сборка
UniversalSearchLauncher-1.0.1-source.zip      исходники без .tools/bin/obj/artifacts
SHA256SUMS.txt                                 контрольные суммы
```

## Только приложение

```powershell
dotnet restore src\UniversalSearchLauncher\UniversalSearchLauncher.csproj -r win-x64
dotnet publish src\UniversalSearchLauncher\UniversalSearchLauncher.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o artifacts\publish
```

## Тесты

```powershell
dotnet restore tests\UniversalSearchLauncher.Tests\UniversalSearchLauncher.Tests.csproj
dotnet run --project tests\UniversalSearchLauncher.Tests\UniversalSearchLauncher.Tests.csproj -c Release
```

Или отдельная проверка одного текста/запроса:

```powershell
dotnet run --project tests\UniversalSearchLauncher.Tests -c Release -- score calc "ArtCraft Launcher"
dotnet run --project tests\UniversalSearchLauncher.Tests -c Release -- score-apps <apps.json> calc
```

## Установщик отдельно

Полная сборка уже делает это. Если `artifacts\payload.zip` готов:

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
  /win32icon:assets\app.ico /win32manifest:installer\setup.manifest `
  /resource:artifacts\payload.zip,Payload.zip `
  /out:dist\UniversalSearchLauncher-Setup-1.0.1.exe `
  installer\Setup.cs `
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.CSharp.dll
```

Деинсталлятор:

```powershell
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
  /win32icon:assets\app.ico /win32manifest:installer\setup.manifest `
  /out:artifacts\Uninstall.exe installer\Uninstaller.cs `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll
```

`Uninstall.exe` должен попасть в payload до его архивации.

## Версия

При выпуске новой версии обновите:

- `src\UniversalSearchLauncher\UniversalSearchLauncher.csproj`: `<Version>`, `<FileVersion>`, `<AssemblyVersion>`;
- `installer\Setup.cs`: `Version` и assembly-атрибуты;
- `installer\Uninstaller.cs`: assembly-атрибуты;
- имя артефактов в `scripts\build-release.ps1` (`$version`);
- `README.md`, `docs\INSTALL.md`.

## Подпись

Для избежания SmartScreen подпишите Setup и приложение сертификатом Authenticode:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
  /f certificate.pfx /p <password> dist\UniversalSearchLauncher-Setup-1.0.1.exe
```

Подписывайте установщик после сборки, а приложение — до упаковки payload, если нужна подпись и у вложенного exe.

## Офлайн-среда

В проверочной среде сеть и профиль пользователя могут быть ограничены. Сборка поддерживает локальный SDK в `.tools\dotnet`, локальные runtime-паки в `.tools\packages` и локальный NuGet-кэш в `.tools\nuget`. Эти папки не входят в исходный архив.
