# stock — A股数据获取与分析工具集

本仓库包含两个独立的 WPF (.NET 8) 桌面程序，围绕"抓数据 → 存数据 → 分析数据"这条线，但彼此解耦，通过约定好的本地数据库文件互相配合（Fetcher 产出，用户手动拷贝给 Analyzer 读取）。

| 程序 | 作用 | 详细文档 |
|---|---|---|
| **StockPlatform.Fetcher**（`src/StockPlatform.Fetcher`） | 数据获取程序：一键抓取全市场A股近3年历史行情（日/周/月线），自动获取股票列表，支持多数据源手动切换 | [doc/data-platform-design.md](doc/data-platform-design.md) |
| **StockPlatform.Analyzer**（`src/StockPlatform.Analyzer`） | 批量分析程序：读取本地数据库，按用户选定的粒度批量分析全市场股票，通过 Tab 切换"峰哥法"/"金叉法"/"耀哥法"/"彬哥法"四种分析方法，图表化展示判断依据 | [doc/analysis-app-design.md](doc/analysis-app-design.md) |

两者共享的底层代码在 `src/StockPlatform.Logic`（领域模型、接口契约、技术指标、分析引擎）和 `src/StockPlatform.Data`（SQLite存储、数据源实现）。

**曾经还有第三个程序** `StockAnalyzer`（实时选股小工具，`src/StockAnalyzer.*`）——它的7指标打分逻辑已经移植成了 Analyzer 的"金叉法"（见 analysis-app-design.md 3.2.2节），确认覆盖完整后该程序连同它自己的说明文档都已从仓库删除（见 analysis-app-design.md 第5节的变更记录）。

## 构建

```
dotnet build StockAnalyzer.sln
```

两个可执行程序分别是 `StockPlatform.Fetcher.exe`、`StockPlatform.Analyzer.exe`。

## 发布

```powershell
.\scripts\publish.ps1
```

编译发布两个程序（Release、win-x64、单文件自包含），更新 `publish/` 目录里的两个 exe——先发布到系统临时目录，确认都编译成功之后才复制过去，不会用 `dotnet publish -o` 直接对着 `publish/` 发（那样做会把目录里认不出来的文件当垃圾清掉，包括 `data/` 里的本地数据）。`publish/data/`（本地数据库、自选股等）不会被这个脚本碰。如果某个程序正在运行，对应的 exe 会更新失败并提示，需要先关掉它再单独重新跑一次脚本；已经在跑的程序不会被自动关闭。

## 文档索引

- [doc/data-platform-design.md](doc/data-platform-design.md) — 数据获取程序的整体设计：多数据源、数据库表结构、限速与反封IP策略（文档第6节记录了一套后来确认从未使用、已废弃的网盘多机共享方案，仅作历史参考）
- [doc/analysis-app-design.md](doc/analysis-app-design.md) — 批量分析程序的设计：峰哥法/金叉法/耀哥法/彬哥法四种分析规则、图表化依据展示
- [Requirement.txt](doc/Requirement.txt) — 用户手工记录的需求笔记，独立维护，不与上述设计文档合并
