# 光子证券交易所（DSP-PhotonStockExchange）开发文档

- 当前版本：**v0.1.1**（正式发布）
- 工程目录：`C:\XCM\DSP MOD\DSP-StockExchange`
- 游戏版本：戴森球计划 0.10.35.29104 + BepInEx 5.4.17 + CommonAPI 1.6.7 + LDBTool 3.0.3
- 发布包：`dist\EnergyCoinExchange-0.1.0.zip`（R2 / Thunderstore 格式）
- 文档更新时间：2026-10-24

---

## 1. 需求来源与目标

来自用户多轮反馈（m00111 → m01224 → m01804 → m02050 → m02380 → m02408 → m02506）：

- 用**分馏塔外观的独特建筑**当交易所，**传送带两侧进、中间出、无延迟**。
- 太阳帆 ↔ 能量币可互转；不同星系有**不同兑换比例**（地区价差）。
- 买卖收**印花税**，面板显示税后价格。
- 价格**周期性刷新并涨跌**（最终定为 10 分钟一期）。
- 用能量币可**兑换临界光子**（固定 1:1，不计税）。
- 解锁绑定「通关科技」；物品 ID 避开星环与创世之书。
- UI 全中文；行情面板（Alt+3）、K 线面板（Alt+4），快捷键可改绑；面板右上角可点击红叉关闭。

## 2. 玩法与数值

| 项目 | 规则 |
| --- | --- |
| 买入能量币 | 两侧投入太阳帆，中间产出能量币；`每币需要帆数 = ceil(基准价 × (1+税率))` |
| 卖出能量币 | 两侧投入能量币，中间产出太阳帆；`每币换帆数 = floor(基准价 × (1-税率))` |
| 兑换光子 | 两侧投入能量币，中间产出临界光子；`消耗 = PhotonCoinCost`（默认 1），不计税 |
| 星系价差 | `价格 = clamp(基准价 × 地区系数 × (1 + 波动), 0.2, 5.0)`，地区系数由 `star.seed/star.index` 哈希得到，范围约 0.7~1.3 |
| 时间波动 | 每个 10 分钟 epoch 变化一次，正弦叠加两个频率 |
| 印花税 | 默认 2%（买卖各一次） |
| 供电 | 默认必须通电才结算；未通电时仓内物品保留 |
| 切换模式 | 仓内物品自动退还玩家 |

## 3. 技术实现

### 3.1 原型注册（CommonAPI ProtoRegistry）

- `RegisterItem(6008, "能量币", ...)`、`RegisterItem(6009, "光子证券交易所", ...)`：`EItemType.Production`，图标用 `GetDefaultIconDesc` 生成。
- `RegisterRecipe(6010, ERecipeType.Assemble, 180, [钢材×8, 石材×4, 玻璃×4, 处理器×1] → [交易所×1])`。
- `RegisterModel(667, item, "Entities/Prefabs/fractionator", null, new[]{53,11,12,1,40}, 0)`：**复用原版分馏塔 prefab**，因此原版放置时自动创建 `FractionatorComponent`，无需自造 prefab/AssetBundle。
- `OnLoadingFinished` 里：复制原版分馏塔 `HpMax / workEnergyPerTick / idleEnergyPerTick`；把 `fracFluidInputMax / fracProductOutputMax / fracFluidOutputMax` 置 0（彻底关闭原版分馏逻辑）；把模型染成金色。
- 建造菜单：`item.BuildIndex = 410`（第 4 页「仓储」第 10 格）。**不能放第 5 页**——该页被 CommonAPI/DSP-MolecularHub 改过，会触发 `UIBuildMenu._OnUpdate` NullReferenceException。

### 3.2 交易状态机（ExchangeLogic.cs）

- **驱动方式**：`MarketOverlay.Update()` 每帧调用 `ExchangeLogic.TickAll()`，按 `GameMain.gameTick` 去重，遍历 `GameMain.data.factories` 中所有工厂。
  - 原因：`FactorySystem.GameTick`（asm_full.cs:98882，调用点 :256220 `FactorySystemFacilityGameTick`）的 Harmony 补丁虽挂上（诊断 `postfixes=1`），但实测方法体从不执行 → 改用每帧驱动（GameTick postfix 保留作备用）。
- **状态存在 `FractionatorComponent` 里**（随存档自动保存，无需自建数据结构）：
  - `fluidInputCount` = 待处理输入物品数量
  - `fluidInputInc` = 兼容旧版遗留的“输入能量币”
  - `productOutputCount` = 待输出能量币
  - `fluidOutputCount` = 待输出太阳帆
  - `progress` = 卖出方向的千分位余量（毫帆）
  - `seed` = 当前模式（1=卖出能量币 / 2=买入能量币 / 3=兑换光子；`incUsed` 是 **bool**，不能用）
- **输入**：两侧口（`belt1/belt2`，`isOutput=false`）用 `CargoTraffic.GetItemIdAtRear` 判断物品、`TryPickItemAtRear` 取货，单缓冲上限 20000。
- **输出**：中间口（`belt0`，`isOutput=true`）用 `CargoTraffic.TryInsertItemAtHead` 每 tick 推 1 件。
- **拆除退还**：`Patches.cs` 覆盖两条路径——正常拆除 `FactorySystem.TakeBackItems_Fractionator`、瞬间拆除 `PlanetFactory.ClearItemsInEntity`。
  - **关键**：Prefix 里必须 `ref FractionatorComponent frac = ref fs.fractionatorPool[fracId];`（用 `out` 拿到的是副本，会导致“不返还”）。

### 3.3 价格服务（PriceService，位于 ExchangeLogic.cs）

- `GetPriceAt(StarData star)`：`FNV(star.seed, star.index)` → 地区系数；`GetEpoch() = GameMain.gameTick / (GameMain.tickPerSec × 60 × PricePeriodMinutes)`；再叠加正弦波动。
- `GetPriceAtContinuous(star, double epoch)`：给 K 线采样用（连续 epoch，不取整）。
- `GetQuotes(price, out buyNeed, out sellGive)`：税后买卖价统一出口。

### 3.4 界面

| 界面 | 实现 |
| --- | --- |
| 行情面板（Alt+3） | IMGUI 固定坐标 560×382；当前星系名 + 基准价 + 涨跌、税后买/卖/光子、期号与税率、最便宜/最贵各 6 个星系（`StarData.displayName` 前 6 字）、底部导航线提示（已实现：点击星系名称切换该星系的导航线） |
| K 线面板（Alt+4） | IMGUI 372×236；36 根蜡烛（每根 8 个采样点算开高低收）、红涨绿跌、价格刻度、金色现价线 |
| 单塔面板 | IMGUI 620×344；显示状态/模式/基准价/税后买/税后卖/兑换光子/仓内，三个模式按钮 + 红叉 |
| 原版分馏塔窗口 | Harmony Postfix 接管 `UIFractionatorWindow._OnUpdate`：标题改「光子证券交易所」，隐藏原料/产物图标与按钮，关闭 `remindText`，用 `historyText` 显示四行（当前模式 / 税后买 / 税后卖 / 两侧传送带提示），在「供电行下方到窗口底端」区间垂直居中、左缘对齐标题 |
| 红叉 | 12×12 像素纹理绘制，保证在 22×22 按钮内上下左右居中 |

- 中文字体：`Font.CreateDynamicFontFromOSFont(new[]{"Microsoft YaHei","Microsoft YaHei UI","SimHei",...}, 14)`。
- 面板鼠标点击用 `Input.mousePosition` 手工命中（DSP 游戏内光标与系统光标不同，自动化点击无法命中，真实鼠标正常）。
- 快捷键：`CustomKeyBindSystem.RegisterKeyBindWithReturn<PressKeyBind>` 注册 `OpenEnergyCoinMarket`（默认 Alt+3）、`OpenEnergyCoinChart`（默认 Alt+4），`canOverride=true` → 出现在游戏「设置 → 按键」。两个面板互斥显示。

## 4. ID 与兼容

| 用途 | ID |
| --- | --- |
| 能量币（物品） | 6008 |
| 光子证券交易所（建筑物品） | 6009 |
| 交易所配方 | 6010 |
| 交易所模型 | 667（复用 `Entities/Prefabs/fractionator`，金色染色） |
| 建造菜单 BuildIndex | 410（第 4 页仓储第 10 格） |
| 默认解锁科技 | 1508（任务完成！） |

- 与 **星环（ProjectOrbitalRing，物品 6201+ / 配方 401–849 / 科技 ≤1992）**、**创世之书（GenesisBook）** 的 ID 区间不重叠。
- 与 **万物分馏（FractionateEverything）** 同样复用分馏塔 prefab，但各用独立模型 ID，互不影响。
- 联机（Nebula 2.1.0.7）未专门适配：单机可正常使用，联机建议自行测试。

## 5. 工程结构与构建

```
DSP-StockExchange/
├─ DSP-StockExchange.csproj   # net472；引用 tools\refs（游戏程序集）+ 游戏 plugins 里的 CommonAPI/LDBTool
├─ manifest.json              # R2 清单
├─ Plugin.cs                  # ID 常量、配置项、Proto 注册、快捷键注册
├─ ExchangeLogic.cs           # 交易状态机 + PriceService
├─ Patches.cs                 # GameTick(备用) / UnlockTech / 两条拆除路径 / 分馏塔窗口文案
├─ MarketOverlay.cs           # IMGUI 面板（行情/K线/单塔）+ 每帧驱动 TickAll
├─ _dev\DevTools.cs.bak       # 开发期热键源码（F1~F19），不参与发布编译
└─ bin\Release\DSP-StockExchange.dll
```

构建（需先设好离线环境变量）：

```powershell
$env:DOTNET_CLI_HOME='C:\XCM\DSP MOD\tools\.dotnet-home'
$env:TEMP='C:\XCM\DSP MOD\tools\.tmp'; $env:TMP=$env:TEMP
$env:NUGET_PACKAGES='C:\XCM\DSP MOD\tools\nuget-packages'
& 'C:\XCM\DSP MOD\tools\dotnet-sdk\dotnet.exe' build .\DSP-StockExchange\DSP-StockExchange.csproj -c Release -v minimal
```

部署测试：**先关闭游戏**（否则 DLL 报 `user-mapped section open`），再复制到
`C:\Program Files (x86)\Steam\steamapps\common\Dyson Sphere Program\BepInEx\plugins\DSP-StockExchange\`。

发布打包：见第 8 节。

## 6. 测试记录（自动化 + 目验）

- 插件加载：`Photon Stock Exchange v0.1.0 loaded (ids: coin=6008, building=6009)`、`protos ready`，无异常。
- 价格：v0.1.0 前实测 `1 coin = 0.93 sails`、最便宜/最贵星系排行、10 分钟期号、税后买/卖/光子行均正确。
- 三模式换算：买入 60 帆 → 60 币（need=ceil(0.89×1.02)=1）；卖出 60 币 → 52 帆 + 余 320 毫（`floor(0.89×0.98×1000)=872`/枚）；兑换光子 60 币 → 60 光子。
- 供电：未通电时输入 60 帆保留、不结算（`power=0.00 working=False`），解除限制后立即转化。
- 拆除返还：`[dev] dismantle-real ... sail 600->610 coin 1158->1218`（真实 `DismantleFinally` 路径），瞬间拆除路径同样通过。
- 建造项：`build-item: id=6009 buildIndex=410 unlocked=True recipeUnlocked=True`。
- 界面：窗口四行不重叠且垂直居中、红叉居中、行情/K线/单塔面板中文完整（截图 `tools\shots\v16_all.png`）。
- 存读档：用户实测仓内物品与模式随存档保存；联机/跨版本兼容未测。

## 7. 已知问题与后续路线

1. **联机同步**：Nebula 多人模式下价格与仓内状态尚未做网络同步（未测试）。
2. 平衡数值（税率 2%、地区差 30%、波动 15%、光子 1:1）为初版，待玩家反馈调整。
3. 联机（Nebula）适配未做。
4. 商品种类目前只有「太阳帆 / 能量币 / 临界光子」，后续可扩展火箭、戴森球组件等。

## 8. 发布流程（R2 / Thunderstore）

发布包结构（zip 根目录）：

```
manifest.json
icon.png            # 256×256 PNG
README.md
CHANGELOG.md
plugins/DSP-StockExchange/DSP-StockExchange.dll
```

- `manifest.json`：`name` / `description` / `version_number` / `dependencies` / `website_url`；依赖只写 `CommonAPI-CommonAPI-1.6.7`（会传递安装 BepInEx、LDBTool、DSPModSave）。
- 发布前检查：`csproj` 里没有 `DEV_TEST` 定义、`DevTools.cs` 不参与编译、DLL 中不含 `DevTools` / `[dev]` / `DevAutoLoad` 字符串（可用 ASCII 扫描确认）。
- 打包结果：`dist\EnergyCoinExchange-0.1.0.zip`；暂存目录 `dist\EnergyCoinExchange\`；图标生成脚本 `tools\icon_gen.py`。

## 9. 关键踩坑（摘要，完整记录见 `procedures\_inbox.md`）

- Harmony 补丁挂上但 `FactorySystem.GameTick` 方法体不执行 → 每帧 `Update` 驱动 + `gameTick` 去重。
- `FractionatorComponent.incUsed` 是 **bool**、`seed` 是 **uint**，都不能存复杂状态；模式存 `seed`。
- STRUCT 传值陷阱：池元素必须 `ref` 取用，`out` 会拿到副本。
- `UIFractionatorWindow` 的 `remindText` 与 `historyText` 占同一区域，同时启用必叠字。
- DSP 游戏内光标 ≠ 系统光标，自动化点击无法命中游戏内按钮；真实鼠标正常。
- 游戏运行中复制 DLL 报 `user-mapped section open` → 先杀进程。
- PowerShell 用 `Get-Content`/`Set-Content` 处理含中文源码会毁编码 → 只用 .NET `[IO.File]::ReadAllText/WriteAllText` + UTF8。

## 10. v0.1.1 更新（2026-10-24）

- **改名**：MOD 与游戏内建筑统一为「光子证券交易所 / Photon Stock Exchange」；能量币物品名不变。
- **中英双语 UI**：`L10N.cs` 按 `Localization.CurrentLanguageLCID`（2052/1028 = 中文）选择文案；配方、物品名与按键名通过 `LocalizationModule.RegisterTranslation` 注册，随游戏语言切换。
- **点击星系名导航线**：行情面板「最便宜 / 最贵」排行每行可点击，`PlayerNavigation.indicatorAstroId = star.astroId` 切换；与目标相同时置 0 取消（与原版星图导航按钮语义一致）。
- **K 线面板重排**：现价并入底栏（`现价 0.93 ｜ 每格 10 分钟 ｜ 红涨绿跌 ｜ 共 36 格`），价格刻度与蜡烛区不再重叠；标题改为「光子证券交易所 · K 线」。
- **面板间距**：行情面板与交易所面板不再相互压边（交易所面板 y=404）。
- **新图标**：用户提供素材，字幕「老实赚钱」改为「老实打球」，输出 256×256 `icon.png` 与 2560×1440 宣传图 `开发文档\assets\photon-stock-exchange-promo.png`。
- **发布包**：`dist\PhotonStockExchange-0.1.1.zip`（manifest name=PhotonStockExchange，dependencies 仍为 CommonAPI-CommonAPI-1.6.7）。

## 物品图标（V0.1.1 补充）

- **能量币（6008）**：内嵌 PNG `Assets\coin.png`（11,349 B，蓝色圆边 + 白色塑料币面 + 黄色闪电），csproj 以 `EmbeddedResource` 的 `LogicalName = StockExchange.coin.png` 打包，运行时 `GetManifestResourceStream` 读取 → `Texture2D.LoadImage` → `Sprite.Create`（100 PPU），再通过反射写 `ItemProto._iconSprite`。
- **光子证券交易所（6009）**：取原版分馏塔（2314）的 `iconSprite`，其纹理 `Icons/ItemRecipe/fractionator` 为 80×80 且 `isReadable = true`，直接 `GetPixels` 后按亮度着金色（`lum = max(r,g,b) * 1.12`，`p.r = lum, p.g = lum * 0.78, p.b = lum * 0.24`），生成新 `Sprite` 写回 `_iconSprite`，与建筑模型金色一致。
- 应用时机：`ProtoRegistry.onLoadingFinished` 回调内（LDB 已完成 Preload，`item.iconSprite` 可用）。
- 注意：`ItemProto.iconSprite` 是只读属性，替换必须用私有字段 `_iconSprite`；图标纹理可读性要判空，不可读时退回默认图标。

## 发布（V0.1.1 正式包）

- 目录：`dist\PhotonStockExchange\`（manifest.json / icon.png / README.md / CHANGELOG.md / plugins\DSP-StockExchange\DSP-StockExchange.dll）
- 压缩包：`dist\PhotonStockExchange-0.1.1.zip`（141,846 B，sha256 `e927a541bd7d9221597fc9cf61d123dddae663e88288358ad10f350316a09862`）
- 正式配置默认：`UnlockTechId = 1508`、`PricePeriodMinutes = 10`、`PhotonCoinCost = 1`、`RequirePower = true`、`EnableTint = true`、`TaxRate = 0.02`。
