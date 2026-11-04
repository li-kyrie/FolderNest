# FolderNest 2.1 架构

2026-11-04。唯一运行框架为 C# / .NET 10 + WPF + SQLite。WinForms 仅用于 NotifyIcon，Microsoft.VisualBasic.FileIO 用于系统删除适配，均在同一自包含 EXE 内。无旧 PowerShell 运行依赖。

## 分层

| 层 | 主要组件 | 职责 |
| --- | --- | --- |
| Core | Models、MonitorValidation | 配置、接口、约束和明确的重置/清理请求 |
| Core | ExpiryPolicy、ReminderPolicy | UTC 计时、到期判断、本地每日提醒 |
| Core | ScanService | 完整枚举后原子替换本目录观察记录，失联保留历史 |
| Core | TimingResetService | 按目录/项目重置，保留未选中记录；每目录全量校验成功后提交 |
| Core | CleanupService | 明确清单确认后逐项复查配置、计时、路径及指纹并清理 |
| Infrastructure | SqliteRepository | SQLite WAL、设置、目录配置/顺序、观察数据和级联删除 |
| Infrastructure | LocalFileInventory、ArrivalTracker | 根文件/一级子文件夹元数据指纹、事件观察、重解析点边界 |
| Infrastructure | WindowsTaskScheduler | 两条全局计划的自动维护、核验、回滚、同数据旧任务归档迁移和所有权保护 |
| Infrastructure | WindowsFileDeletion | 拒绝链接祖先/后代，调用回收站/永久删除 |
| Infrastructure | PortablePaths、RollingAuditLog、LegacyImporter | 便携路径、总额日志、显式只读旧 JSON 转换 |
| Desktop | App | WinExe、图标、单实例/数据锁、托盘、Dispatcher 轮询 |
| Desktop | MainViewModel、MonitorRow | 配置状态、串行门控、自动调度、合法编辑、稳定行对象 |
| Desktop | MainWindow、Dialogs、MonitorActions、Controls | 目录管理、选择保持、排序、添加/删除/重置/设置/到期清单和整数微调 |

## 生命周期

1. 解析可选 `--data` 并定位数据。互斥量和物理文件锁阻止同数据目录并发运行；第二实例正常启动唤回主窗口，后台启动发送检查信号。
2. 加载旧/新 SQLite 记录，保留 ID 和时间锚点，忽略 2.0 的 Scheduled 字段。没有重新导入或默认重置。
3. 有任意监控就自动确保 `FolderNest Daily`、`FolderNest Logon` 两个计划有效；全部目录停用不卸载计划，最后一条监控删除后移除计划。每 5 分钟校验计划，可恢复外部误删/禁用；异常在 UI 和日志展示。
4. 正常启动显示主窗口；`--background` 只驻留托盘。当前用户交互令牌、不存密码。已有监控时关闭窗口收起，明确退出停止本次进程，不删除启动计划。
5. Dispatcher 每 3 秒判断；启动/每天到点补扫，每 15 分钟校正，事件静默约 2 秒后合并扫描。所有 Enabled 目录都参与，不再有“部署目录选择”。
6. ApplyActivity 先刷新对应一级单元 LastActivity，再完整枚举/提交。观察队列约 10000 上限，溢出保守刷新并重扫。扫描、重置、清理共用 SemaphoreSlim；配置/排序在 Dispatcher 空闲时提交。
7. 到指定时间、当天未提醒、当轮成功扫描存在到期项才通知。最近提醒日期保存，清单不持久冻结，不追催。
8. 用户打开清单重扫，选择/模式覆盖/二次确认后构造 CleanupRequest。成功删除才移除记录，失败留记录；扫描/提醒不调用清理。

## 加入、停用、移除、重置

添加目录先枚举库存，以加入时 UTC 初始化已存在项目；枚举失败不新增配置。保存配置后保存观察失败会撤销新增配置。在线新项目首次发现时计时，修改按观察时刻刷新。

停用只改 Enabled，保留时间锚点，不冻结日历；重新启用后检查变化。移除监控级联删除自己的观察及排序条目，不碰磁盘文件，重新添加使用新 ID/新计时。路径修改清空该 ID 旧路径历史，新路径项目下次扫描从首次发现开始。

重置窗口只读取库存，不改变计时；允许显式查看/重置停用目录。整目录选择用 Paths=null，执行时枚举全部当前单元；子项选择传大小写不敏感路径集合。没有勾选不执行。指定项目消失/变链接/目录不可访问时整组失败，不提交部分结果、不产生成功逐项日志。不同目录分别报告结果。

一级子文件夹按整体计时，不声称能独立重置任意嵌套文件。重置不修改文件日期/内容、不改变 Enabled。

截止时间为 max(FirstSeenUtc, LastActivityUtc) + RetentionDays。关机时只是截止时间继续推进，不可能实际扫描；未运行期间的真实进入时刻不可恢复，首次发现保守开始。

## 排序与选择

monitor_order 表独立保存顺序，迁移缺失条目按原 rowid 排列；SaveOrder 在事务中校验完整 ID 集合，拒绝丢项/重复/失效列表。排序不重写计时。

ObservableCollection.Move 保留行对象；Ctrl/Shift 使用原生 Extended 选择，空白拖动不框选。拖动仅从左侧柄开始，需超过系统阈值；Up/Down 按钮提供鼠标/键盘可达替代，支持批量所选行稳定移动。禁用列临时排序，避免与持久人工顺序混淆。

## Windows 计划保护

任务的存在、启用、触发器类型/时间、用户、程序路径、参数及数据目录都参与核验。更新/移除先验证当前用户、FolderNest 描述和完全匹配数据参数，拒绝覆盖同名外部任务。部署失败恢复原 XML/删除新建项；日志记名称、路径、时间及绑定目录。

默认名称不带版本。新计划成功后，对当前用户 SID 的旧 FolderNest 2 计划，仅同数据所有权验证通过才备份 XML 并移除；异数据计划只记跳过。测试使用随机 namePrefix，不执行正式旧任务迁移。

同一正式任务名只服务一套数据。移动程序但保留数据绝对路径可自动更新 EXE；移动数据路径需处理旧计划，不能静默接管另一套数据。

## 存储、发布与边界

SQLite monitors 保存类型化 JSON，observations 以 monitor_id/path 为键，settings 保存程序设置/最近提醒日期，monitor_order 保存显示顺序。DDL 增量创建新表，不重写观察数据。

首选 EXE 旁 data，已有 data-v2 优先复用；不可写回退 LocalAppData。主窗口显示实际位置；显式 --data 无效直接报错。日志默认总计 10 MiB，当前/一个轮换文件各一半，设置缩额在下次写入生效。

--smoke-test 使用独立临时库验证启动，不改 Windows 任务；--no-schedule 是开发诊断开关，禁用自动任务管理/后台轮询，仅保留手动操作，不用于日常启动。

ZIP 按白名单仅 EXE/README/CHANGELOG/LICENSE/docs，不含个人库/日志/归档或 SDK。单文件原生库可解压到系统运行时缓存。

不承诺关机物理扫描、USN 离线重放、内容级版本识别、Windows 通知可见时长、网络盘回收站支持或抵御恶意并发替换。元数据指纹不等于内容哈希；实际删除前的路径复查不是文件句柄级沙箱。
