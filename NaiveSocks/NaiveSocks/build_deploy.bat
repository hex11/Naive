@echo off

set nzip=..\..\NaiveZip\NZip\bin\Release\NZip.exe

set bindir=bin\release
set deploydir=%bindir%\naivesocks
if not [%1]==[] set deploydir=%1
set singlefile=%deploydir%\NaiveSocks_SingleFile.exe
set files=NaiveSocks.exe NaiveSvrLib.dll Nett.dll LiteDB.dll

call :info copying files to deploy dir...

call :mkdir %deploydir%
call :info2 copying binarys...
robocopy %bindir%\ %deploydir%\ %files% /NJH /NJS

call :info2 copying configration example...
copy ..\naivesocks-example.tml %deploydir%\ || goto :failed

echo.
call :info building single file edition...
if not exist %nzip% (
	call :info2 NaiveZip executable not found! ^(%nzip%^)
	goto :failed
)
%nzip% pe %bindir%\NaiveSocks.exe %bindir%\Nett.dll %bindir%\NaiveSvrLib.dll "" %singlefile% || goto :failed

call :info Finished building deploy!!
call :info2 [deploy dir]: %deploydir%
call :info2 [single-file edition]: %singlefile%
echo.

if not "%1"=="nopause" pause

:: "function"s:

goto :eof
:failed
popd
call :info Failed.
if not "%1"=="nopause" pause
goto :eof

:mkdir
if not exist %1 (
	call :info2 dir %1 does not exist, creating...
	mkdir %1
)
goto :eof

:info
echo ==^> %*
goto :eof

:info2
echo   -^> %*
goto :eof
