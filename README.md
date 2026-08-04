# 桌面事项贴

一款仅在本机运行的 Windows 桌面便利贴应用。每张便利贴包含可勾选待办，支持桌面常驻、始终置顶、项目进度、提醒、归档、回收站和本地备份。

## 开发构建

需要 .NET 10 SDK：

```powershell
dotnet build DesktopTaskNotes.slnx -c Release
dotnet run --project DesktopTaskNotes/DesktopTaskNotes.csproj
dotnet run --project DesktopTaskNotes.Tests/DesktopTaskNotes.Tests.csproj -c Release
```

## 发布

```powershell
dotnet publish DesktopTaskNotes/DesktopTaskNotes.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish
```

数据默认保存在 `%LOCALAPPDATA%\DesktopTaskNotes`，自动备份默认保存在“文档\桌面事项贴备份”。应用不包含任何网络功能。
