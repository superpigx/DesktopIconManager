@echo off

title 桌面图标管理器



echo.

echo ============================================

echo   构建 桌面图标管理器

echo ============================================

echo.



set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe

if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe



if not exist "%CSC%" (

    echo   [错误] 未找到 C# 编译器 csc.exe

    echo.

    echo   解决方案：安装 Visual Studio Build Tools

    echo   下载: https://visualstudio.microsoft.com/downloads/

    echo.

    pause

    exit /b 1

)



cd /d "%~dp0"

if not exist bin mkdir bin



echo   编译器: %CSC%

echo   正在编译...

echo.



rem console 子系统：GUI 可正常开窗，命令行模式也能看到输出

"%CSC%" /nologo /target:exe /platform:anycpu /codepage:65001 /optimize+ /win32icon:app.ico ^

    /out:bin\DesktopIconManager.exe ^

    /reference:System.dll ^

    /reference:System.Core.dll ^

    /reference:System.Drawing.dll ^

    /reference:System.Windows.Forms.dll ^

    Program.cs ^

    Core\Classifier.cs ^

    Core\DesktopIconReader.cs ^

    Core\NativeMethods.cs ^

    Core\DesktopLayer.cs ^

    Core\IconLayoutEngine.cs ^

    Core\PositionStore.cs ^

    Core\DesktopToggler.cs ^

    Core\GroupLayout.cs ^

    Core\AppSettings.cs ^

    Core\ExplorerWatcher.cs ^

    Core\ShellLauncher.cs ^

    Core\ShellContextMenu.cs ^

    Core\AutoStartManager.cs ^

    UI\ThemePalette.cs ^

    UI\IconExtractor.cs ^

    UI\GroupPanelRenderer.cs ^

    UI\PreviewForm.cs ^

    UI\OverlayNative.cs ^

    UI\DesktopOverlayForm.cs ^

    UI\SnapEngine.cs ^

    UI\ThemeBinder.cs ^

    UI\SettingsForm.cs ^

    UI\TrayHost.cs



if errorlevel 1 (

    echo.

    echo   [失败] 编译出错，请查看上方错误信息。

    echo.

    pause

    exit /b 1

)



echo.

echo   [成功] 已生成: bin\DesktopIconManager.exe

echo.



:menu

echo ============================================

echo   选择操作

echo ============================================

echo.

echo   【核心功能】

echo     [S] 桌面整理      - 接管桌面，常驻托盘，无控制台窗口

echo     [C] 桌面整理(调试) - 同上，但保留控制台便于看日志

echo.

echo   【样式预览】

echo     [7] 浅色主题    [8] 深色主题    [9] 玻璃主题

echo.

echo   【分析，只读，不改桌面】

echo     [1] 只读探测    [2] 诊断    [3] 列出坐标    [4] 预览布局

echo.

echo   【高级：系统图标重排，会改图标位置】

echo     [5] 应用布局    [6] 还原

echo.

echo   【应急】

echo     [R] 恢复系统桌面图标（万一图标不见了就用这个）

echo     [T] 设置          - 主题 / 尺寸 / 吸附 / 分类规则

echo.

echo     [0] 退出

echo.

set /p choice=请输入选项:



if /i "%choice%"=="S" bin\DesktopIconManager.exe --shell

if /i "%choice%"=="C" bin\DesktopIconManager.exe --shell --console

if "%choice%"=="1" bin\DesktopIconManager.exe --probe

if "%choice%"=="2" bin\DesktopIconManager.exe --diagnose

if "%choice%"=="3" bin\DesktopIconManager.exe --positions

if "%choice%"=="4" bin\DesktopIconManager.exe --preview

if "%choice%"=="5" bin\DesktopIconManager.exe --apply

if "%choice%"=="6" bin\DesktopIconManager.exe --restore

if "%choice%"=="7" bin\DesktopIconManager.exe --ui light

if "%choice%"=="8" bin\DesktopIconManager.exe --ui dark

if "%choice%"=="9" bin\DesktopIconManager.exe --ui glass

if /i "%choice%"=="R" bin\DesktopIconManager.exe --show-icons

if /i "%choice%"=="T" bin\DesktopIconManager.exe --settings

if "%choice%"=="0" exit /b 0



echo.

pause

goto menu