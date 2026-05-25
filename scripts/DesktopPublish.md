# 灵简图片助手 Desktop 发包说明

## 脚本位置

`scripts/Publish-LingJian-Desktop.ps1`

## 支持目标

- `win-x64`
- `win-arm64`
- `osx-x64`
- `osx-arm64`
- `all`

## 支持打包模式

- `folder`
- `single-file`
- `all`

## 常用命令

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-LingJian-Desktop.ps1 -Runtime win-x64 -PackageMode all
```

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-LingJian-Desktop.ps1 -Runtime osx-arm64 -PackageMode folder
```

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-LingJian-Desktop.ps1 -Runtime all -PackageMode single-file
```

如果已经完成过对应 RID 的 restore，也可以追加 `-NoRestore`。

## 输出目录

产物会输出到 `dist` 目录，命名规则：

`灵简图片助手-desktop-<runtime>-<stable|portable>-<timestamp>`

说明：

- `stable` 对应 `folder`
- `portable` 对应 `single-file`

每次 publish 都会同时生成：

- 发布目录
- 对应 zip 包

## 运行文件

- Windows 会重命名为 `灵简图片助手.exe`
- macOS/Linux 风格目标会重命名为 `灵简图片助手`

## 配置说明

- 脚本默认使用仓库根目录的 `NuGet.Config`
- 脚本会把 `APPDATA` 和 `DOTNET_CLI_HOME` 指向仓库内可写目录，避免受当前系统用户目录权限影响

## 当前已验证

- `win-arm64` `folder`
- `win-arm64` `single-file`
- `win-x64` `folder`
- `win-x64` `single-file`
- `osx-x64` `folder`
- `osx-x64` `single-file`
- `osx-arm64` `folder`
- `osx-arm64` `single-file`

## 后续正式发包建议

- Windows 正式版再补签名
- macOS 正式版在 Mac 上完成签名与公证
- 发版前运行 `dotnet list .\ModernImageViewer.slnx package --vulnerable` 检查依赖安全告警
