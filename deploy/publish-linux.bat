@echo off
chcp 65001 >nul 2>&1
setlocal enabledelayedexpansion
REM ============================================================
REM  在 Windows 上发布 Linux 产物，打成 tar.gz 等着上传。
REM
REM  用法：在项目根目录下双击，或
REM        deploy\publish-linux.bat [输出目录]
REM        默认输出 D:\Publish\DtuServer-linux
REM
REM  产出的是「自包含」产物：服务器上不用装 .NET，
REM  但仍然需要 libicu（这项目全是中文，绕不过去）。
REM ============================================================

set "OUT=%~1"
if "%OUT%"=="" set "OUT=D:\Publish\DtuServer-linux"
set "PKG=%OUT%.tar.gz"

pushd "%~dp0.." || (echo 找不到项目根目录 & exit /b 1)

echo.
echo [1/5] 清理旧产物 %OUT%
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%PKG%" del /q "%PKG%"

echo.
echo [2/5] 发布 linux-x64 自包含产物（第一次会下载运行时包，慢是正常的）
dotnet publish -c Release -r linux-x64 --self-contained true -o "%OUT%"
if errorlevel 1 (
  echo.
  echo ！发布失败，上面有编译错误。把错误全文发我，别往下走。
  popd & exit /b 1
)

echo.
echo [3/5] 检查产物完整性
set "BAD="
if not exist "%OUT%\MaxChemical.DtuServer"   (echo   ！缺主程序 MaxChemical.DtuServer & set BAD=1)
if not exist "%OUT%\appsettings.json"        (echo   ！缺 appsettings.json & set BAD=1)
if not exist "%OUT%\wwwroot\shell.css"       (echo   ！缺 wwwroot & set BAD=1)
if not exist "%OUT%\wwwroot\templates"       (echo   ！缺 wwwroot\templates（物模型示例文件）& set BAD=1)
if defined BAD (popd & exit /b 1)
echo   主程序、配置、wwwroot、物模型模板 都在

echo.
echo [4/5] 确认密钥没混进产物
set "LEAK="
if exist "%OUT%\appsettings.Production.json"  (echo   ！appsettings.Production.json 混进来了，删除 & del /q "%OUT%\appsettings.Production.json" & set LEAK=1)
if exist "%OUT%\appsettings.Development.json" (del /q "%OUT%\appsettings.Development.json")
findstr /s /i /m "LTAI" "%OUT%\*.json" >nul 2>&1 && (
  echo   ！产物里还有疑似阿里云密钥的字符串，停下来手工检查：
  findstr /s /i /m "LTAI" "%OUT%\*.json"
  popd & exit /b 1
)
if not defined LEAK echo   干净

echo.
echo [5/5] 打包 %PKG%
tar -czf "%PKG%" -C "%OUT%" .
if errorlevel 1 (
  echo   ！tar 打包失败。Windows 10 1803 以上自带 tar；老系统就直接整个目录传过去。
  popd & exit /b 1
)

for %%F in ("%PKG%") do set "SZ=%%~zF"
set /a SZMB=!SZ!/1048576
echo.
echo ════════════════════════════════════════════
echo  完成： %PKG%   约 !SZMB! MB
echo.
echo  下一步，上传到服务器后执行：
echo    sudo mkdir -p /opt/dtuserver
echo    sudo tar -xzf ~/DtuServer-linux.tar.gz -C /opt/dtuserver
echo    sudo chmod +x /opt/dtuserver/MaxChemical.DtuServer
echo.
echo  ★ chmod +x 一定不能漏 —— Windows 打的包没有可执行位，
echo    漏了 systemd 会报 203/EXEC 起不来。
echo ════════════════════════════════════════════
popd
endlocal
