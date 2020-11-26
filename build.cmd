@echo off
copy %cd%\build\ship\x86\2017\*.lib %cd%\build\ship\x86\
copy %cd%\build\ship\x64\2017\*.lib %cd%\build\ship\x64\
copy build\ship\x86\vs.wixlib   src\ext\VSExtension\wixext\bin\Release
copy build\ship\x86\dutil.lib   src\ext\VSExtension\ca\bin\Release
copy build\ship\x86\wcautil.lib src\ext\VSExtension\ca\bin\Release
msbuild wix.proj /t:Build "/p:PlatformSdkBinPath=C:\Program Files (x86)\Windows Kits\10\bin\10.0.18362.0\x86\;Configuration=Release;PlatformToolset=v142" /p:"DisableSpecificCompilerWarnings=4996;4091;4458;4564" /m
