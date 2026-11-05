# 项目结构与文件职责

```text
FolderNest/
├─ src/                     核心源码：Core / Infrastructure / Desktop
├─ tests/                   行为和 WPF 桌面组件测试源码
├─ scripts/build.ps1        正式构建/测试/发布工具，不是临时脚本
├─ examples/                受版本管理的 JSON 导入模板
├─ docs/                    使用、配置、架构、需求、验收文档
├─ 产品/FolderNest/          本机完整便携产品，不进入源码 Git
│  ├─ FolderNest.exe         日常唯一启动入口
│  ├─ README.md / CHANGELOG.md / LICENSE
│  ├─ docs/ / examples/      配套说明和模板
│  └─ data/                 私有配置、计时、日志、升级备份
├─ 发布包/                  干净 ZIP 和 SHA256，上传 GitHub Release
├─ FolderNest.slnx          解决方案入口
├─ Directory.Build.props    共享编译参数和版本号
├─ FolderNest.ico           项目实际引用的原始图标资源
└─ README.md / CHANGELOG.md / LICENSE
```

Core：到期/提醒规则、扫描、重置、清理复查、接口和模型，不依赖 WPF。

Infrastructure：SQLite、文件/回收站、监听、Windows 计划、便携路径、轮换日志、JSON 导入校验。

Desktop：WPF GUI、托盘、单实例、后台调度、合法编辑、导入预览和用户确认。

src/tests 下 bin、obj 以及 artifacts 都可再生，整理后不保留；本机 SDK 缓存不是源码、不随软件分发。build.ps1 是可复现构建工具，必须保留。

Git 留存源码、测试、文档、模板、图标和版本标签。GitHub Release 存放可运行 ZIP 及校验文件。个人数据库、日志、备份不进入 Git 或公共 Release。
