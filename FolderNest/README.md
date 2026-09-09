# FolderNest

FolderNest 是一个面向 Windows 的本地文件夹保管期限管理工具。它把“监控总目录下的文件”和“一级子文件夹”视为清理单元，按规则积累到期清单，并在你确认后移入回收站或永久删除。

> 当前仓库提供的是无需安装 .NET 的 PowerShell 5.1 便携原型。程序逻辑、配置格式和任务计划入口已按后续单文件 EXE 版本设计。

## 功能

- 每天凌晨 02:00 扫描一次；关机错过后，在用户登录时补扫。
- 根目录下的单个文件独立计时。
- 一级子文件夹作为整体处理，内部递归统计所有文件和子目录。
- 文件夹有效时间取“首次发现时间”和“内部最新文件修改时间”中较晚者。
- 新下载但保留旧修改时间的文件，首次发现时不会立即被误判为过期。
- 到期项目保存为待处理清单；没有清单时不弹出提醒。
- Windows 托盘气泡提醒，提供 WinForms 复选框清单。
- 支持移入回收站（默认）或永久删除。
- 日志达到 10 MB 后自动截断旧内容，仅保留近期记录。
- 便携运行，整个目录可以放在 D 盘、E 盘或其他本地磁盘。

## 快速开始

### 便携运行

将整个 `FolderNest` 文件夹复制到任意位置，例如 `D:\FolderNest`，双击 `FolderNest.cmd` 即可打开清单窗口。

### 安装每日任务

以当前用户身份运行：

```powershell
cd D:\FolderNest
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\FolderNest.ps1 -Install
```

按提示填写监控总目录、保留天数（例如 `30`）和删除模式（`RecycleBin` 或 `Permanent`）。安装脚本会创建 `FolderNest Daily`（每天 02:00）和 `FolderNest Logon`（登录补扫）两个任务。

卸载任务：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\FolderNest.ps1 -Uninstall
```

## 手动命令

```powershell
# 扫描并在有待处理项目时提醒
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\FolderNest.ps1 -Run

# 直接打开待处理清单
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\FolderNest.ps1 -Review

# 修改监控目录、保留天数和删除模式
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\FolderNest.ps1 -Settings
```

## 数据和日志位置

便携版默认将数据放在程序旁：

```text
FolderNest\data\
├── config.json       配置
├── state.json        首次发现时间和待处理状态
└── foldernest.log    扫描、提醒和删除日志（上限 10 MB）
```

如果程序目录不可写，会自动回退到 `%LOCALAPPDATA%\FolderNest\`。监控目录中的原始文件不会被复制或上传，程序只保存路径、时间、大小、状态等元数据。

## 安全行为

- 通知消失、点击“取消”或不操作，都不会删除文件。
- “永久删除”需要在清单窗口再次确认；建议优先使用回收站模式。
- 文件或文件夹已被手动移动/删除时，程序会记录并跳过，不会因单项失败中断整次扫描。
- 请勿将监控总目录设置为系统目录、程序目录或包含本工具数据目录的父目录。

## 项目结构

```text
FolderNest.ps1             主程序
FolderNest.cmd             便携启动入口
Install-FolderNest.cmd     安装任务计划
Uninstall-FolderNest.cmd   卸载任务计划
```

## 许可证

本项目采用 [MIT License](./LICENSE)。
