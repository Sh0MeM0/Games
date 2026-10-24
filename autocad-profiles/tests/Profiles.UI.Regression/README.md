# Проверка меню поперечных профилей

Проект компилирует исходный `SectionPointGrid` и открывает настоящие окна и меню
WinForms без AutoCAD. Ошибка или несработавшая проверка возвращает код 1.

```sh
dotnet build tests/Profiles.UI.Regression/Profiles.UI.Regression.csproj -c Release
```

На Windows запустите
`tests/Profiles.UI.Regression/bin/Release/net472/Profiles.UI.Regression.exe`.
Требуется .NET Framework 4.7.2 или новее.

На Linux с Mono, WinForms и Xvfb:

```sh
xvfb-run -a mono tests/Profiles.UI.Regression/bin/Release/net472/Profiles.UI.Regression.exe
```

Проверяются 15 циклов открытия и закрытия с разными причинами закрытия,
перенос ролей между колонками, обновление отметок выбора и подписей меню,
сохранность расстояний и отметок, перезагрузка точек и освобождение меню
при уничтожении таблицы. Проверки вызывают обработчик кнопки «Назначить»
и реальные события меню; нативные нажатия мыши не эмулируются.

Mono не реализует `Control.DeviceDpi`, поэтому только тестовый проект использует
производный `DataGridView`, предоставляющий 96 DPI на Mono и фактический DPI
на Windows. Код поставляемого модуля этот адаптер не включает. Эти проверки
не заменяют проверку DPI, интеграции и нажатий в AutoCAD на Windows.
