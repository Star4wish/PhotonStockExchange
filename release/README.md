# 光子证券交易所 (Photon Stock Exchange)

在戴森球计划的宇宙里开一间交易所：把 **太阳帆** 换成 **能量币**，再用能量币买回太阳帆，或者直接 **兑换临界光子**。
每个星系都有自己的一套价格，价格每 10 分钟刷新一次、有涨有跌 —— 在便宜的星系买入、在贵的星系卖出，就是本 MOD 的玩法。

Open your own stock exchange in the Dyson Sphere Program universe: turn **solar sails** into **energy coins**, spend those coins back on sails, or exchange them for **critical photons**.
Every star system has its own price. Prices refresh every 10 minutes and move up and down — buy low in cheap systems, sell high in expensive ones. That's the game.

## 玩法 / Gameplay

- 交易所是一座 **分馏塔外观的建筑**（金色涂装，建造菜单 → 仓储页第 10 格）。
- **两侧传送带进货，中间传送带出货，没有延迟**（不需要等生产进度）。
- 点开交易所可以看到当前星系汇率，并在三种模式之间切换：
  1. **卖出能量币（币 → 太阳帆）**：投入能量币，输出太阳帆；
  2. **买入能量币（太阳帆 → 币）**：投入太阳帆，输出能量币；
  3. **兑换光子（币 → 光子）**：投入能量币，输出临界光子（默认 1:1，不计税）。
- 切换模式时，仓内尚未取走的物品会 **自动退还** 给玩家。
- 汇率 = 星系基准价 × 时间波动，每个星系价格不同；**买入收 2% 印花税，卖出也收 2%**，面板上显示的都是 **税后** 价格。
- 价格每 **10 分钟** 刷新一次（可配置）。
- 行情面板里点击 **星系名称** 可以直接在星图上添加 / 取消导航线。

- The exchange is a building that uses the **fractionator model** (gold tint, build menu → Storage page, slot 10).
- **Side belts feed in, the middle belt outputs — no delay** (no crafting progress needed).
- Click the exchange to see the local rate and switch between three modes:
  1. **Sell coins (coins → sails)**: feed energy coins, get solar sails;
  2. **Buy coins (sails → coins)**: feed solar sails, get energy coins;
  3. **Buy photons (coins → photons)**: feed energy coins, get critical photons (default 1:1, no tax).
- Switching mode **refunds** everything still buffered inside the building.
- Rate = system base price × time wave; every system differs. **2% stamp duty on both buy and sell**; all numbers shown are **after tax**.
- Prices refresh every **10 minutes** (configurable).
- Click a **system name** in the market panel to toggle its navigation marker on the starmap.

## 快捷键 / Hotkeys

| 按键 Key | 功能 Action |
| --- | --- |
| `Alt + 3` | 打开 / 关闭光子证券交易所行情（全星系最便宜/最贵排行）· Toggle market panel (cheapest/dearest systems) |
| `Alt + 4` | 打开 / 关闭当前星系的能量币 K 线 · Toggle candle chart of the current system |

按键都可以在游戏的 **设置 → 按键** 里改成自己喜欢的组合。/ Both hotkeys can be rebound in **Settings → Key Bindings**.

## 建筑说明 / Building notes

- 需要 **通电** 才会工作，未通电时仓内物品会保留、不会丢失。· Needs **power**; buffered items are kept while unpowered.
- 拆掉建筑（无论是普通拆除还是瞬间拆除）时，仓内物品与缓冲都会退还到背包。· Demolishing the building (normal or instant dismantle) refunds buffered items to your inventory.
- 交易所可以存读档，仓内物品数量会随存档保存。· Buffer contents are saved with the game.

## 配置 / Configuration

配置文件 / Config file: `BepInEx/config/com.xcm.dsp.stockexchange.cfg`

| 配置项 Key | 默认值 Default | 说明 Description |
| --- | --- | --- |
| TaxRate | 0.02 | 印花税（买卖各收一次）· Stamp duty on buy and sell |
| BasePrice | 1.00 | 能量币基准价（1 能量币值多少太阳帆）· Base price (sails per coin) |
| RegionSpread | 0.30 | 星系之间的价格差异幅度 · Price spread between systems |
| Volatility | 0.15 | 时间波动幅度 · Time volatility |
| PricePeriodMinutes | 10 | 价格刷新周期（分钟）· Price refresh period in minutes |
| RequirePower | true | 是否必须通电才工作 · Require power |
| PhotonCoinCost | 1 | 兑换 1 枚临界光子需要多少能量币 · Coins per critical photon |
| UnlockTechId | 1508 | 解锁交易所配方所需的科技（1508 = 任务完成！）· Tech that unlocks the recipe |
| EnableTint | true | 是否把建筑染成金色 · Tint the building gold |

## 安装 / Installation

- **R2 / Thunderstore**：直接安装，依赖会自动处理。· Install directly; dependencies are handled automatically.
- **手动安装 Manual**：需要先装 [CommonAPI](https://thunderstore.io/c/dyson-sphere-program/p/CommonAPI/CommonAPI/)（它会自动带上 BepInEx 与 LDBTool），然后把 `plugins/DSP-StockExchange/DSP-StockExchange.dll` 放到 `BepInEx/plugins/DSP-StockExchange/`。
  Install [CommonAPI](https://thunderstore.io/c/dyson-sphere-program/p/CommonAPI/CommonAPI/) first (it brings BepInEx and LDBTool), then put `plugins/DSP-StockExchange/DSP-StockExchange.dll` into `BepInEx/plugins/DSP-StockExchange/`.

## 兼容性 / Compatibility

- 物品 ID / Item IDs：能量币 `6008`、光子证券交易所 `6009`；配方 `6010`；模型 `667`；建造菜单位置 `BuildIndex = 410`。
- 与 **星环（ProjectOrbitalRing）**、**创世之书（GenesisBook）** 的 ID 区间不重叠，可同时安装。· ID ranges do not overlap with ProjectOrbitalRing / GenesisBook.
- 与 **万物分馏（FractionateEverything）** 同用分馏塔原型，但各自注册独立模型，互不影响。· Shares the fractionator prefab with FractionateEverything but registers its own model.

## 已知问题 / Known issues

- 暂无 / None.

### 物品图标 / Item icons

- 能量币：蓝色圆边、白色塑料币面、黄色闪电。
- 光子证券交易所：原版分馏塔图标按建筑金色调色。

- Energy coin: blue rim, white plastic face, yellow lightning bolt.
- Photon Stock Exchange: the vanilla fractionator icon tinted gold to match the building.
