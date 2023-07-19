@echo off

cd /d %~1
if "%~2" equ "net461" (
    ILRepack.exe /log /wildcards /internalize /out:.\repack\ILRepack.dll /target:library ILRepack.exe BamlParser.dll Fasterflect.dll Mono.Cecil.dll Mono.Cecil.Mdb.dll Mono.Cecil.Pdb.dll Mono.Cecil.Rocks.dll Mono.Posix.dll
) else if "%~2" equ "netcoreapp3.1" (
    dotnet ILRepack.dll /log /wildcards /internalize /out:.\repack\ILRepack.dll /target:library ILRepack.dll BamlParser.dll Fasterflect.dll Mono.Cecil.dll Mono.Cecil.Mdb.dll Mono.Cecil.Pdb.dll Mono.Cecil.Rocks.dll runtimes\win-x64\lib\netstandard2.0\Mono.Posix.NETStandard.dll
)


@REM if $(TargetFramework) == net461 (
@REM   $(OutDir)ILRepack.exe /log /wildcards /internalize /ndebug /target:library /out:$(OutDir)\repack\ILRepack.dll   $(OutDir)ILRepack.exe $(OutDir)Fasterflect.dll $(OutDir)BamlParser.dll $(OutDir)Mono.Cecil.dll $(OutDir)Mono.Cecil.Mdb.dll $(OutDir)Mono.Cecil.Pdb.dll $(OutDir)Mono.Cecil.Rocks.dll $(OutDir)Mono.Posix.dll
@REM ) ELSE (
@REM   $(OutDir)ILRepack.exe /log /wildcards /internalize /ndebug /target:library /out:$(OutDir)\repack\ILRepack.dll   $(OutDir)ILRepack.dll $(OutDir)Fasterflect.dll $(OutDir)BamlParser.dll $(OutDir)Mono.Cecil.dll $(OutDir)Mono.Cecil.Mdb.dll $(OutDir)Mono.Cecil.Pdb.dll $(OutDir)Mono.Cecil.Rocks.dll $(OutDir)runtimes\win-x64\lib\netstandard2.0\Mono.Posix.NETStandard.dll
@REM )