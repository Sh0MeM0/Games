# Готовая portable-программа

Откройте [StampExtractor-Portable-1.0.0.exe](https://github.com/Sh0MeM0/Games/blob/stamp-extractor/downloads/StampExtractor-Portable-1.0.0.exe)
и нажмите **Download raw file**. Запустите скачанный файл; установка не требуется.
Предназначено для Windows 10/11 x64. Размер — около 95 МиБ.

Файл хранится в Git LFS. Для скачивания через Git нужны Git LFS и `git lfs pull`.
ZIP исходников может содержать только указатель вместо `.exe`.

Сверить файл в PowerShell:

```powershell
Get-FileHash .\StampExtractor-Portable-1.0.0.exe -Algorithm SHA256
```

Ожидаемая сумма — в `SHA256SUMS.txt`. Подпись издателя не добавлена.
Запуск самой portable-оболочки на настоящей Windows ещё не проверен;
приложение проверено в Electron под Linux, содержимое Windows-пакета проверено.

[Инструкция](../stamp-extractor/README.md) · [Отчёт проверок](../stamp-extractor/VALIDATION.md)
