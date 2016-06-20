@ECHO OFF

SET WORKING_FOLDER=%~dp0tmp

SET /p VERSION=<version

RMDIR /s /q %WORKING_FOLDER%
MKDIR %WORKING_FOLDER%

"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" .\AWSSAMLCredentials\AWSSAMLCredentials.sln /p:Configuration=Release

::package
ECHO ON
nuget pack -Verbosity quiet -OutputDirectory %WORKING_FOLDER% -Version %VERSION%
@ECHO OFF

GOTO :DONE

:DONE
  ECHO completed
  EXIT /B %ERRORLEVEL%
:EOF
  EXIT /B %ERRORLEVEL%