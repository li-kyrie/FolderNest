# FolderNest 2.0 架构

2026-11-03。唯一运行框架为 C# / .NET 10 + WPF；SQLite 保存状态，WinForms 仅提供系统 NotifyIcon，Microsoft.VisualBasic.FileIO 提供 Windows Shell 删除适配。它们都是同一 EXE 内的 .NET 系统库，不再加载旧 PowerShell。

## 分层与职责

| 层 | 主要文件 | 职责 |
| --- | --- | --- |
| Core | Models、MonitorValidation | 类型、接口、目录/天数约束 |
| Core | ExpiryPolicy、ReminderPolicy | 首次观察和活动时间锚点、到期判断、当地每日提醒策略 |
| Core | ScanService | 完整枚举成功后替换本目录记录；失败保留历史、不放入本轮到期清单 |
| Core | CleanupService | 只接受明确清单请求；删除前检查配置、记录、归属、指纹和期限，逐项审计 |
| Infrastructure | SqliteRepository | 参数化 SQLite，WAL，原子替换观察记录，设置保存，目录删除级联元数据 |
| Infrastructure | LocalFileInventory | 根文件/一级子文件夹清理单元、递归元数据指纹、不遍历重解析点 |
| Infrastructure | ArrivalTracker | FileSystemWatcher，子目录事件、重命名、溢出重同步 |
| Infrastructure | WindowsTaskScheduler | COM 计划任务部署/回滚/卸载和实际状态核对 |
| Infrastructure | WindowsFileDeletion | 删除前拒绝链接祖先及后代，再调用系统回收站/永久删除 |
| Infrastructure | PortablePaths、RollingAuditLog、LegacyImporter | 可写路径探测、总额受限日志、只读旧配置转换 |
| Desktop | App | GUI WinExe、图标、数据目录单实例、物理锁、托盘和计时器 |
| Desktop | MainViewModel、MonitorRow | UI 状态、串行操作、INotifyDataErrorInfo、只保存合法配置 |
| Desktop | MainWindow、Dialogs、Controls | 目录管理、任务勾选、设置、到期清单、整数微调和选择交互 |

## 运行流

1. App 解析可选的 `--data`，定位真实数据目录。命名互斥量防重复进程，物理独占文件锁阻止别名路径并发打开相同库。再次正常启动发送显示事件；任务启动发送检查信号。
2. 正常启动显示主窗口；`--background` 仅启动托盘。任务以当前用户交互令牌运行，不要求服务、不保存密码。
3. WPF Dispatcher 每 3 秒检查状态。自动任务完整部署时，仅处理 Scheduled + Enabled 的目录；启动/每日到点/错过计划后补扫，每 15 分钟全量校正，事件发生约 2 秒后合并校正。
4. 观察事件先刷新对应一级清理单元的 LastActivity，再枚举与提交。队列上限约 10000，溢出或 watcher 错误时保守刷新计时并重扫，宁可推迟而不提前删除。
5. 自动检查与手动扫描/清理共用 SemaphoreSlim，操作期间 GUI 配置入口不可用。配置和任务选择从 UI Dispatcher 提交，避免扫描中切换目录。
6. 到指定时间且当天尚未提醒且存在本次成功扫描的到期项时发通知；持久化最近提醒日期，不保存 Pending 清单。无人操作不启动删除。
7. 用户打开清单先重扫，点击执行并二次确认后才构造 CleanupRequest。删除前再次验证；成功后才移除对应记录，失败留记录并显示原因。

截止时间为 max(FirstSeenUtc, LastActivityUtc) + RetentionDays。首次观察不用文件原修改时间；未运行期间的真实进入时刻不可可靠还原。文件指纹为排序后的相对路径、类型、大小、修改/创建时间 SHA-256；这是**元数据指纹**，不是文件内容哈希。

## 目录与任务状态

行内“参与/不参与”是配置选择，“实际任务状态”来自 COM 核对，不是勾选即成功。核对任务是否存在、是否启用、操作 EXE/参数、用户、触发类型与每日时间。未部署/部分部署/未知状态不会启动自动扫描或发通知。

每用户两个全局任务，不为每目录单独创建任务。部署前至少一个启用且勾选的目录；两个任务都验证成功后原子保存目录选择。单个目录删除或取消参与不删除全局任务，但不再扫描该目录；卸载全局任务不清空配置。移动程序需重新部署。

## 存储和发布

SQLite 的 monitors 表保存类型化 JSON 配置；observations 以 monitor_id/path 为键；settings 保存程序设置和最后提醒日期。删除监控使用外键级联清理观察数据，不触碰真实文件；改路径仅重置该 ID 的观察历史。停用保留历史但不冻结日历。

首选 EXE 旁 data；已有 data-v2 库优先复用，程序目录不可写时回退 LocalAppData。允许高级命令行 `--data "绝对目录"`；传入路径无效时直接报错，不静默换库。自包含单文件 EXE 中的原生 DLL 会由 .NET 提取到运行时缓存，不承诺机器上只产生一个文件。

日志默认 10 MiB，总额分为当前和一个轮换文件，额度调整后的下一次写入执行收缩。归档只删除本程序轮换日志，不删除其他文件。

正式发布白名单为 EXE、README、CHANGELOG、LICENSE 和 docs，不含源码工具链或个人数据。

## 已知边界

不实现关机扫描、Windows 服务、USN Journal 离线重放、跨平台或内容级版本识别。Windows 通知受系统设置限制；回收站可恢复性受磁盘和系统限制。路径复查可防常见变化/链接替换，但不是抵御恶意并发攻击的原子文件句柄删除机制。README 明确这些边界，不夸大可靠性。
