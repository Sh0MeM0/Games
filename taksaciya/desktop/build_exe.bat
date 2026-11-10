@echo off
rem Сборка Таксация Лайт в один файл TaksaciyaLite.exe (нужен Python 3.10+ для Windows с python.org).
rem Запускать из папки taksaciya:  desktop\build_exe.bat
setlocal
cd /d "%~dp0\.."
python -m pip install --upgrade pyinstaller || goto :err
python tests\test_engine_py.py || goto :err
python -m PyInstaller --noconfirm --onefile --windowed --name TaksaciyaLite ^
  --add-data "core\reference.json;core" --paths . desktop\taksaciya_app.py || goto :err
dist\TaksaciyaLite.exe --selftest || goto :err
echo.
echo Готово: dist\TaksaciyaLite.exe
exit /b 0
:err
echo Сборка не удалась.
exit /b 1
