#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>
#include <stdio.h>
#include <stdlib.h>

/* No shell is involved: arguments stay in the Windows Unicode command line. */
static int launch(const wchar_t *arguments, int console) {
    wchar_t root[32768], python[32768], command[32768], logpath[32768];
    DWORD length = GetModuleFileNameW(NULL, root, 32768);
    if (!length || length >= 32767) return 2;
    wchar_t *slash = wcsrchr(root, L'\\');
    if (!slash) return 2;
    *slash = 0;
    if (swprintf(python, 32768, L"%ls\\runtime\\%ls", root,
                 console ? L"python.exe" : L"pythonw.exe") < 0) return 2;
    if (GetFileAttributesW(python) == INVALID_FILE_ATTRIBUTES) {
        MessageBoxW(NULL, L"Не найдена папка runtime. Распакуйте весь архив программы или повторите установку.",
                    L"PDF Мастер", MB_ICONERROR);
        return 2;
    }
    SetEnvironmentVariableW(L"PDF2OFFICE_HOME", root);
    SetEnvironmentVariableW(L"PYTHONUTF8", L"1");
    SetEnvironmentVariableW(L"PYTHONDONTWRITEBYTECODE", L"1");
    if (swprintf(command, 32768, L"\"%ls\" -X utf8 -B -m pdf2office %ls", python, arguments) < 0) return 2;
    STARTUPINFOW startup = {0};
    PROCESS_INFORMATION process = {0};
    startup.cb = sizeof(startup);
    HANDLE log = INVALID_HANDLE_VALUE;
    if (!console && GetEnvironmentVariableW(L"LOCALAPPDATA", logpath, 32768)) {
        size_t used = wcslen(logpath);
        if (used < 32600) {
            wcscat(logpath, L"\\PDFMaster");
            CreateDirectoryW(logpath, NULL);
            wcscat(logpath, L"\\startup.log");
            SECURITY_ATTRIBUTES security = {sizeof(security), NULL, TRUE};
            log = CreateFileW(logpath, GENERIC_WRITE, FILE_SHARE_READ, &security,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
            if (log != INVALID_HANDLE_VALUE) {
                startup.dwFlags |= STARTF_USESTDHANDLES;
                startup.hStdOutput = log;
                startup.hStdError = log;
                startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
            }
        }
    }
    BOOL ok = CreateProcessW(python, command, NULL, NULL, TRUE,
                             console ? 0 : CREATE_NO_WINDOW, NULL, NULL,
                             &startup, &process);
    if (log != INVALID_HANDLE_VALUE) CloseHandle(log);
    if (!ok) {
        MessageBoxW(NULL, L"Не удалось запустить программу. Проверьте целостность папки runtime.",
                    L"PDF Мастер", MB_ICONERROR);
        return 2;
    }
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD code = 0;
    GetExitCodeProcess(process.hProcess, &code);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    if (code && !console)
        MessageBoxW(NULL, L"Программа завершилась с ошибкой. Подробности: %LOCALAPPDATA%\\PDFMaster\\startup.log",
                    L"PDF Мастер", MB_ICONERROR);
    return (int)code;
}

#ifdef CLI_LAUNCHER
int wmain(void) {
    const wchar_t *args = GetCommandLineW();
    if (*args == L'\"') { args++; while (*args && *args != L'\"') args++; if (*args) args++; }
    else { while (*args && *args != L' ') args++; }
    while (*args == L' ') args++;
    return launch(args, 1);
}
#else
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show) {
    (void)instance; (void)previous; (void)show;
    return launch(arguments, 0);
}
#endif
