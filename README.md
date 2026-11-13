# Печати и подписи 4.0 — portable для Windows

[**Скачать StampExtractor-Portable-x64-4.0.0.zip**](https://github.com/Sh0MeM0/Games/raw/stamp-extractor/downloads/StampExtractor-Portable-x64-4.0.0.zip)

Распакуйте архив целиком и запустите `StampExtractor-Portable-x64\StampExtractor.exe`.
Установка, интернет и права администратора не нужны. Windows 10/11 x64,
используется системный Microsoft Edge WebView2 Runtime.

Программа открывает PDF (все страницы), сканы, фотографии и скриншоты, находит
печати и подписи локальной нейросетью и сохраняет каждый объект отдельным PNG
на белом фоне. Печатный текст, линии бланка и рукописные записи убираются.

[Инструкция и ограничения](stamp-extractor/README.md) ·
[Проведённые проверки](stamp-extractor/VALIDATION.md) ·
[Модель и её обучение](stamp-extractor/model/README.md) ·
[Контрольные суммы](downloads/SHA256SUMS.txt)

Модель обучена на синтетических документах и может ошибаться на реальных:
проверяйте каждый объект перед сохранением.

Предыдущие версии 1.0–3.0 (один EXE на Electron, Git LFS) оставлены в `downloads/`.
