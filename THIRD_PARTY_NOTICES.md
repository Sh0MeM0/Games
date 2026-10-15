# PDF Мастер — лицензии и исходный код

Код PDF Мастер распространяется по GNU AGPL версии 3 или более поздней
(полный текст — `LICENSE`). Это свободная программа без гарантий. Компоненты
сохраняют собственные авторские права и лицензии. Коммерческая лицензия Artifex
в эту поставку не входит: для PyMuPDF/MuPDF используется AGPL.

Поставка работает локально, без обязательной установки AutoCAD, ODA, Python или
Tesseract. Шрифты сторонних коммерческих продуктов не включены. Права на
дополнительно импортированные пользователем шрифты остаются у правообладателей.

| Компонент | Версия | Лицензия / исходники |
|---|---|---|
| CPython | 3.12.10 | PSF-2.0; https://github.com/python/cpython/tree/v3.12.10 |
| PySide6 Essentials, Shiboken6, Qt | 6.8.3 | LGPL-3.0 / GPL-3.0; https://github.com/pyside/pyside-setup/tree/v6.8.3 ; https://github.com/qt/qtbase/tree/v6.8.3 |
| PyMuPDF | 1.26.6 | AGPL-3.0; https://github.com/pymupdf/PyMuPDF/tree/1.26.6 |
| MuPDF (в PyMuPDF) | 1.26.11 | AGPL-3.0; https://github.com/ArtifexSoftware/mupdf/tree/1.26.11 |
| GNU LibreDWG | 0.14 | GPL-3.0-or-later; https://github.com/LibreDWG/libredwg/releases/download/0.14/libredwg-0.14.tar.xz |
| PCRE2 (в поставке LibreDWG) | 10.47 | BSD-3-Clause; https://github.com/PCRE2Project/pcre2/tree/pcre2-10.47 |
| GNU libiconv (в поставке LibreDWG) | 1.18 | LGPL-2.1-or-later; https://ftp.gnu.org/pub/gnu/libiconv/libiconv-1.18.tar.gz |
| Tesseract | 5.5.3 | Apache-2.0; https://github.com/tesseract-ocr/tesseract/archive/refs/tags/5.5.3.tar.gz |
| tessdata_fast (eng, rus, osd) | 4.1.0 | Apache-2.0; https://github.com/tesseract-ocr/tessdata_fast/tree/4.1.0 |
| ezdxf | 1.4.4 | MIT; https://github.com/mozman/ezdxf/tree/v1.4.4 |
| python-docx | 1.2.0 | MIT; https://github.com/python-openxml/python-docx/tree/v1.2.0 |
| Pillow | 12.3.0 | MIT-CMU/HPND; исходный архив указан в `DEPENDENCIES.json` |
| fonttools | 4.60.1 | MIT; https://github.com/fonttools/fonttools/tree/4.60.1 |
| NumPy | 2.2.6 | BSD-3-Clause и лицензии OpenBLAS; https://github.com/numpy/numpy/tree/v2.2.6 |
| OpenCV headless | 4.11.0.86 | Apache-2.0 и лицензии зависимостей; https://github.com/opencv/opencv/tree/4.11.0 ; https://github.com/opencv/opencv-python/tree/86 |
| lxml | 6.0.2 | BSD и лицензии libxml2/libxslt; исходный архив указан в `DEPENDENCIES.json` |
| pyparsing, typing_extensions | версии в `DEPENDENCIES.json` | MIT / PSF; тексты в `licenses/python` |
| osifont, Liberation | см. `assets/fonts` | лицензии и сведения об авторах рядом с файлами шрифтов |

Полные тексты основных свободных лицензий находятся в `licenses/general` и
`assets/licenses`. Сохранены оригинальные лицензии Python-пакетов в
`runtime/Lib/site-packages/*.dist-info` и `licenses/python`. Лицензии, точные
версии, исходные URL, патчи и сборочные рецепты библиотек Tesseract находятся в
`licenses/conda/<имя>/licenses`, `licenses/conda/<имя>/recipe` и `index.json`.
В частности, поставка содержит Leptonica, libarchive, libpng, libjpeg-turbo,
libtiff, libwebp, OpenJPEG, GIFLIB, zlib, zstd, xz/liblzma, bzip2, LZ4, LZO,
libdeflate, LERC, libxml2, libiconv, ICU, libcurl, libssh2, libpsl, Kerberos,
OpenSSL и распространяемые Microsoft VC Runtime/UCRT. Их лицензии применяются
отдельно. Ни одна коммерческая лицензия Qt не заявляется: используются права
открытых LGPL/GPL лицензий.

Qt и другие LGPL библиотеки загружаются динамически. Замена совместимых DLL и
отладка таких замен не запрещаются. Python-код не зашифрован и лежит в `app`;
исходники приложения, тесты, C launcher и сценарии сборки приложены отдельно в
`PDFMaster-Source.zip`. Для модификации достаточно заменить исходники и
совместимые динамические библиотеки либо пересобрать дистрибутив. Проверка
подписи, запрещающая запуск модифицированной версии, не применяется.

## Получение соответствующих исходников зависимостей

`DEPENDENCIES.json` рядом с исполняемым файлом содержит прямой URL каждого
использованного бинарного пакета, SHA-256 и URL исходного архива для Python-
зависимостей. Для conda-пакетов точные URL, контрольные суммы исходников и патчи
есть в приложенных `recipe/rendered_recipe.yaml`, `recipe/recipe.yaml` или
`recipe/meta.yaml`. Эти рецепты относятся именно к включённым версиям.
Исходники LibreDWG приложены в `assets/licenses/libredwg-0.14.tar.xz` и также
доступны по прямой ссылке в таблице; исходники Qt 6.8.3:
https://download.qt.io/archive/qt/6.8/6.8.3/single/qt-everywhere-src-6.8.3.tar.xz
и https://github.com/pyside/pyside-setup/archive/refs/tags/v6.8.3.tar.gz .
Исходники PyMuPDF также доступны по прямому URL `source` в `DEPENDENCIES.json`;
этот архив содержит сборочный рецепт точной версии MuPDF и инструкции сборки.
Сведения об исходниках нужно передавать вместе с бинарной поставкой.

Архивы получены через HTTPS с проверкой TLS. Пакеты PyPI и conda сверены с
SHA-256, опубликованными в метаданных соответствующего реестра. Все входящие
артефакты закреплены контрольными суммами в `packaging/windows-assets.lock.json`.
Подписи OpenPGP Windows-архива и исходников LibreDWG проверены ключом
`38A4167B0DB69E49C5F7216CB8C28866AB27A7A2`, полученным с HTTPS-страницы
сопровождающего проекта https://github.com/rurban.gpg . Ключ и подпись бинарной
поставки приложены в `packaging/keys` исходного архива; сборка повторяет проверку
подписи. Это проверка ключа, опубликованного на GitHub, без независимой
сертификации личности его владельца. Созданные EXE не имеют
коммерческой Authenticode-подписи издателя.
