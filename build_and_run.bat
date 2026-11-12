@echo off
chcp 65001 >nul
cd /d "%~dp0XiangqiOverlay"
echo 正在关闭已有程序实例...
taskkill /f /im XiangqiOverlay.App.exe >nul 2>nul
taskkill /f /im pikafish.exe >nul 2>nul

echo 正在编译构建项目...
dotnet build XiangqiOverlay.sln --configuration Debug
if %ERRORLEVEL% NEQ 0 (
    echo [错误] 编译失败，错误代码: %ERRORLEVEL%
    pause
    exit /b %ERRORLEVEL%
)

set BIN_DIR=%~dp0XiangqiOverlay\src\XiangqiOverlay.App\bin\Debug\net8.0-windows
if not exist "%BIN_DIR%\OpenCvSharpExtern.dll" (
    if exist "%BIN_DIR%\runtimes\win-x64\native\OpenCvSharpExtern.dll" (
        copy /y "%BIN_DIR%\runtimes\win-x64\native\*" "%BIN_DIR%\" >nul
    )
)

echo.
echo 正在启动 XiangqiOverlay...
cd /d "%BIN_DIR%"
start "" XiangqiOverlay.App.exe
echo 启动成功！
