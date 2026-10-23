# Сторонние компоненты

Модуль «Профиля» распространяется по AGPL-3.0-or-later (текст — LICENSE.txt в bundle и LICENSE в корне репозитория).

В поставку входит Newtonsoft.Json 13.0.3, Copyright (c) 2007 James Newton-King, по лицензии MIT:

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Официальные пакеты AutoCAD.NET / AutoCAD.NET.Core / AutoCAD.NET.Model принадлежат Autodesk и используются только как ссылки при компиляции. Их DLL не распространяются: рабочие библиотеки предоставляет установленный лицензионный AutoCAD.

Microsoft.NETFramework.ReferenceAssemblies.net472 — только ссылки для сборки; среду .NET Framework предоставляет Windows. Тестовые зависимости xUnit и Microsoft.NET.Test.Sdk в установщик не входят.

Установщик создан NSIS 3.11. Код загрузчика и установщика NSIS распространяется по лицензии zlib/libpng; используемые стандартные компоненты MUI2, System и код сжатия — на условиях, приведённых в документации NSIS. Исходники: https://nsis.sourceforge.io/ и https://sourceforge.net/projects/nsis/files/. Компилятор NSIS в поставку не входит.

Форматирование профилей разработано по предоставленным пользователем LISP-шаблонам. В присланном MDSTools.exe изучен стандартный способ регистрации частичного CUIX-меню через PackageContents.xml. Собственный файл Profiles.cuix сформирован заново; код, команды, изображения и DLL MDSTools не используются и не включены в дистрибутив. Пользовательский DWG и исходные LISP-файлы в публичные файлы не включены.
