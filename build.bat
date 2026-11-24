@echo off
chcp 65001 >nul
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /platform:x64 /codepage:65001 /target:winexe /optimize+ /win32icon:app.ico ^
  /r:System.Xaml.dll ^
  /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll ^
  /r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\WPF\WindowsBase.dll ^
  /out:PdfToTiff.exe Program.cs
if %errorlevel% neq 0 (
    echo.
    echo Compilation failed!
    pause
    exit /b 1
)
echo.
echo Built PdfToTiff.exe successfully!
pause
