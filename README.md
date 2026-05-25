# 灵简图片助手

灵简图片助手是一款基于 Avalonia 和 .NET 的桌面图片管理工具，面向本地图片浏览、筛选、预览、批量处理和导出场景。

当前项目版本：`1.6`

## 主要功能

- 本地图片文件夹扫描和图片列表管理
- 大图预览、缩略图预热和预览缓存
- 图片信息、尺寸、EXIF 等元数据读取
- 图片导出、压缩、格式转换和批量处理
- 相似图片分析、完全重复图片检测
- 文件重命名、文件操作和最近会话记录
- Windows 和 macOS 桌面端打包

## 项目结构

```text
src/
  ModernImageViewer.Core/       核心图片集合与公共逻辑
  ModernImageViewer.Desktop/    Avalonia 桌面端应用

tests/
  ModernImageViewer.Tests/      功能测试
  ModernImageViewer.PerfTests/  性能测试入口和测试数据构造

scripts/
  Publish-LingJian-Desktop.ps1  桌面端发布脚本
  Create-MacDmg.sh              macOS dmg 辅助脚本
  DesktopPublish.md             打包说明
```

## 环境要求

- .NET SDK `10.x`
- Windows 10/11，或支持 .NET/Avalonia 的 macOS 环境
- 第一次还原 NuGet 依赖时需要联网

可以用下面命令确认本机 .NET 环境：

```powershell
dotnet --info
```

## 构建

在仓库根目录执行：

```powershell
dotnet build .\ModernImageViewer.slnx
```

## 运行

```powershell
dotnet run --project .\src\ModernImageViewer.Desktop\ModernImageViewer.Desktop.csproj
```

## 测试

运行功能测试：

```powershell
dotnet test .\tests\ModernImageViewer.Tests\ModernImageViewer.Tests.csproj
```

运行性能测试入口：

```powershell
dotnet run --project .\tests\ModernImageViewer.PerfTests\ModernImageViewer.PerfTests.csproj
```

## 打包

发布脚本位于：

```text
scripts/Publish-LingJian-Desktop.ps1
```

打包 Windows x64 单文件版本：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-LingJian-Desktop.ps1 -Runtime win-x64 -PackageMode single-file
```

打包 Windows x64 文件夹版和单文件版：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-LingJian-Desktop.ps1 -Runtime win-x64 -PackageMode all
```

打包所有已配置平台的单文件版本：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-LingJian-Desktop.ps1 -Runtime all -PackageMode single-file
```

支持的运行时目标：

```text
win-x64
win-arm64
osx-x64
osx-arm64
all
```

支持的打包模式：

```text
folder       文件夹版
single-file  单文件版
all          同时打包两种模式
```

打包完成后，产物会输出到 `dist/`：

```text
dist/灵简图片助手-desktop-<runtime>-<stable|portable>-<timestamp>/
dist/灵简图片助手-desktop-<runtime>-<stable|portable>-<timestamp>.zip
```

说明：

- `folder` 模式对应 `stable`
- `single-file` 模式对应 `portable`
- Windows 启动文件会命名为 `灵简图片助手.exe`
- macOS 目标会生成 `.app` 结构，正式发布前建议在 macOS 上完成签名和公证
