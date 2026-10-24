# 光子证券交易所 / Photon Stock Exchange

Dyson Sphere Program（戴森球计划）MOD：把太阳帆与「能量币」做成可交易的资产，在不同星系的交易所按当地汇率即时兑换，价格随时间涨跌——低买高卖，做宇宙里的炒股大亨。

A Dyson Sphere Program mod: turn solar sails and “energy coins” into tradable assets. Each star system's exchange offers its own instant rate, and prices move over time — buy low, sell high.

## 玩法 / Gameplay

- 交易所是一个分馏塔外观的建筑：**两侧传送带进货，中间传送带出货，无延迟**。
- 三种模式（点击建筑界面切换，切换时仓内物品退还）：
  - 卖出能量币：能量币 → 太阳帆
  - 买入能量币：太阳帆 → 能量币（按当前星系汇率）
  - 兑换光子：能量币 → 临界光子（1:1，可配置）
- 星系间存在地区价差，价格每 10 分钟刷新一次；买卖各收 2% 印花税（可配置）。
- `Alt+3` 打开行情面板：当前星系价格、税后买卖价、最便宜/最贵星系排行；点击星系名称可在星图上添加/取消导航线。
- `Alt+4` 打开当前星系的能量币 K 线图（36 根蜡烛，每格 10 分钟）。
- 交易所需要通电；拆除建筑或切换模式会退还仓内物品；仓内物品随存档保存。
- 界面语言跟随游戏语言：中文环境显示中文，其它语言显示英文。

## 安装 / Install

1. 安装 BepInEx 5.4.x 与 [CommonAPI](https://github.com/xiaoye97/DSPCommonAPI)（依赖 `CommonAPI-CommonAPI-1.6.7`）。
2. 把 `plugins/DSP-StockExchange/DSP-StockExchange.dll` 放入 `BepInEx/plugins/DSP-StockExchange/`。
3. 启动游戏。建筑配方默认绑定科技 `1508`（任务完成！）解锁；想开局即用可把 `BepInEx/config/com.xcm.dsp.stockexchange.cfg` 里 `UnlockTechId` 改成 `0`。

## 构建 / Build

```powershell
# 引用默认从 Steam 安装目录读取；游戏不在默认路径时用 -p:GameDir 覆盖
dotnet build .\src\DSP-StockExchange.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Dyson Sphere Program"
```

需要 .NET SDK（net472 目标）、BepInEx 5 与 CommonAPI；编译引用来自游戏安装目录的 `BepInEx\core`、`BepInEx\plugins\CommonAPI-CommonAPI`、`DSPGAME_Data\Managed`。

## 目录 / Layout

```
src/        MOD 源码（C#，BepInEx 5 + CommonAPI）
docs/       开发文档（中文，含技术实现与踩坑）
release/    可直接发布的包内容（manifest/README/CHANGELOG/图标/DLL/zip）
assets/     图标、宣传图、游戏内截图
```

## 说明 / Notes

- 物品 ID：能量币 `6008`、交易所 `6009`、配方 `6010`、模型 `667`、建造菜单槽位 `410`。
- 与「星环」「创世之书」等 MOD 的物品 ID 段做了避让。
- 已知限制：建造/拆除、传送带接线等交互需要玩家手动操作；多人（Nebula）未做同步适配。

## 致谢 / Credits

- 参考了 [万物分馏 FractionateEverything](https://github.com/starfi5h/...)（MLJ_DSPmods）中自定义分馏塔的注册方式。
- 感谢 BepInEx、CommonAPI、LDBTool 等社区工具。
