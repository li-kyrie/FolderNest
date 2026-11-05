# 监控配置导入

入口为“导入监控配置”，支持自己编写的 JSON 和旧脚本监控配置。模板在 `examples/monitor-config.example.json`，先改成自己的真实路径。

```json
{
  "Monitors": [
    { "Path": "D:\\下载暂存", "Days": 30, "DeleteMode": "RecycleBin", "Enabled": true }
  ]
}
```

- 标准 JSON，不支持注释/尾逗号，字段区分大小写且不能重复。
- Monitors 必须为非空数组，也支持顶层直接一个目录对象。
- Path：真实可访问的绝对目录；不允许磁盘根、链接路径、程序/数据目录及其上下级。兼容旧 Root 字段，但不能与 Path 同填。
- Days：可省略（默认 30），只能是 1–36500 的 JSON 整数，不接受字符串/小数。
- DeleteMode：可省略（默认 RecycleBin），只允许 RecycleBin 或 Permanent。
- Enabled：可省略（默认 true），只接受布尔值 true/false。
- 旧 Id、Scheduled 字段忽略；其他未知字段报错，不会静默使用默认值。
- 导入内或与现有监控重复/包含的路径拒绝。语法/预校验失败时不新增任何目录。

校验后预览目录、天数、方式、启用状态，取消不执行。确认后逐条新增，执行中若目录消失等情况分别报告成功数/失败原因，成功项保留；不是跨目录全有全无事务。

只导入设置，以新 ID 从导入时计时；不恢复旧 Pending/FirstSeen，不覆盖现有监控。源 JSON 不修改、不持续同步。

运行时配置在底部显示的 foldernest.db（SQLite），与计时共用数据库。请用 GUI 修改，不直接编辑运行中的 DB。要保留旧 SQLite 计时，应退出并备份整个 data 后复用，不用 JSON 导入恢复。
