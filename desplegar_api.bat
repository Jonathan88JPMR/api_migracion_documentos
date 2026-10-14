@echo off
setlocal

REM ============================================================
REM Publica la API y la copia al sitio IIS del servidor.
REM Uso:
REM   desplegar_api.bat [\\SERVIDOR\C$\api_migracion_doc]
REM
REM IMPORTANTE: NO copia web.config ni appsettings*.json.
REM El web.config del servidor contiene <environmentVariables>
REM con SQL_PASSWORD (no va en el repo). Si se sobrescribe con el
REM generado por dotnet publish, la API pierde la clave y cae al
REM fallback de config_modo.bat (entorno PRUEBA / errores de
REM validacion). Los appsettings del servidor pueden tener rutas
REM propias; tampoco se tocan.
REM ============================================================

set "DESTINO=%~1"
if "%DESTINO%"=="" set "DESTINO=\\172.16.20.3\c$\api_migracion_doc"

set "ORIGEN=%~dp0"
set "PUB=%ORIGEN%bin\Release\net8.0\publish"

echo [ORIGEN]  %PUB%
echo [DESTINO] %DESTINO%

echo [BUILD] dotnet publish -c Release ...
dotnet publish "%ORIGEN%api_migracion_documentos.csproj" -c Release -v q
if errorlevel 1 (echo [ERROR] Fallo la compilacion & exit /b 1)

if not exist "%PUB%\api_migracion_documentos.dll" (
    echo [ERROR] No se genero %PUB%\api_migracion_documentos.dll
    exit /b 1
)

if not exist "%DESTINO%" (
    echo [ERROR] No existe %DESTINO%
    exit /b 1
)

echo [OFFLINE] app_offline.htm (libera locks de DLL en IIS)...
echo API en mantenimiento > "%DESTINO%\app_offline.htm"
timeout /t 3 /nobreak >nul

echo [COPIA] DLLs y recursos (sin web.config ni appsettings)...
robocopy "%PUB%" "%DESTINO%" /E /XF web.config appsettings.json appsettings.*.json /NFL /NDL /NJH /R:2 /W:2
if errorlevel 8 (echo [ERROR] Fallo la copia & goto :online)

echo [ONLINE] quitando app_offline.htm...
del /q "%DESTINO%\app_offline.htm" 2>nul

echo [VERIFICACION]
dir /B "%DESTINO%\api_migracion_documentos.dll" "%DESTINO%\web.config" 2>nul
echo ============================================================
echo [OK] API desplegada en %DESTINO%
echo ============================================================
goto :fin

:online
del /q "%DESTINO%\app_offline.htm" 2>nul

:fin
endlocal
