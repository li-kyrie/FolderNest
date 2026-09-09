# FolderNest

FolderNest 是一个 Windows 本地文件夹保管期限管理工具。它把监控总目录下的直接文件和一级子文件夹作为清理单元：子文件夹内部递归统计，内部任何文件更新都会刷新整个文件夹的有效时间。

## 当前版本

这是一个无需安装 .NET 的 PowerShell 5.1 便携版原型，支持：

- 每天 02:00 扫描和用户登录补扫；
- 首次发现时间，避免旧文件下载后立即误判；
- 持久化待处理清单；
- Windows 托盘气泡提醒；
- WinForms 勾选清单；
- 移入回收站或永久删除；
- 10 MB 日志上限；
- 可将程序目录放在任意磁盘。

## 使用

双击 `Install-FolderNest.cmd`，按提示填写监控目录、保留天数和删除模式。手动运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\FolderNest.ps1 -Run
powershell -ExecutionPolicy Bypass -File .\FolderNest.ps1 -Review
```

默认配置和状态保存在 `%LOCALAPPDATA%\FolderNest`。后续正式版将把这部分改为可在设置中选择的数据目录，并发布单文件 EXE 安装包和便携 ZIP。

## 安全说明

忽略通知不会删除文件。永久删除只在清单窗口中再次确认；默认建议使用回收站模式。程序不会上传文件内容。
