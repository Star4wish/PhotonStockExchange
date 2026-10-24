using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using CommonAPI;
using CommonAPI.Systems;
using CommonAPI.Systems.ModLocalization;
using HarmonyLib;
using UnityEngine;

namespace StockExchange
{
    /// <summary>
    /// 星际能量币交易所：入口插件。
    /// 分馏塔模型 + 三传送带口（两侧进、中间出）的即时兑换建筑。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency(CommonAPIPlugin.GUID)]
    [CommonAPISubmoduleDependency(nameof(ProtoRegistry), nameof(LocalizationModule), nameof(CustomKeyBindSystem))]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.xcm.dsp.stockexchange";
        public const string NAME = "Photon Stock Exchange";
        public const string VERSION = "0.1.1";

        // ===== 本 MOD 原型 ID（已避开星环 6201+、分子枢纽 6007/666/1999；创世之书未安装） =====
        // 注意：建筑类物品 ID 必须 < 32768 —— EntityData.protoId 是 short，超过会溢出。
        public const int ItemCoin = 6008;           // 能量币
        public const int ItemExchange = 6009;       // 能量币交易所（建筑）
        public const int RecipeExchange = 6010;     // 建筑配方
        public const int ModelExchange = 667;       // 建筑模型（666 已被分子枢纽占用）
        // 建造菜单第 4 页（仓储）第 10 格 = 410；第 5 页（生产）的 511 会让 UIBuildMenu._OnUpdate 报 NRE
        public const int BuildIndexExchange = 410;

        // ===== 复用原版 =====
        public const int VanillaItemFractionator = 2314;
        public const int VanillaModelFractionator = 119;
        public const int ItemSunSail = 1501;
        public const int ItemSmallRocket = 1503;
        public const int ItemCriticalPhoton = 1208;

        // 建材
        private static readonly int[] ExchangeRecipeInputs = { 1103, 1108, 1110, 1303 }; // 钢材 / 石材 / 玻璃 / 处理器
        private static readonly int[] ExchangeRecipeCounts = { 8, 4, 4, 1 };

        public static Plugin Instance { get; private set; }
        public static bool Registered { get; private set; }

        internal static ItemProto CoinItem;
        internal static ItemProto ExchangeItem;
        internal static RecipeProto ExchangeRecipe;
        internal static ModelProto ExchangeModel;

        /// <summary>行情面板开关（可在游戏「设置 → 按键」里改绑，默认 Alt+3）。</summary>
        public static PressKeyBind ToggleMarketKey;

        /// <summary>K 线图开关（可在游戏「设置 → 按键」里改绑，默认 Alt+4）。</summary>
        public static PressKeyBind ToggleChartKey;

        public static ConfigEntry<double> TaxRate;
        public static ConfigEntry<double> BasePrice;
        public static ConfigEntry<double> RegionSpread;
        public static ConfigEntry<double> Volatility;
        public static ConfigEntry<int> PricePeriodMinutes;
        public static ConfigEntry<int> PhotonCoinCost;
        public static ConfigEntry<int> UnlockTechId;
        public static ConfigEntry<bool> EnableTint;
        public static ConfigEntry<bool> VerboseLog;
        public static ConfigEntry<bool> RequirePower;
        private static int errorLogged;

        private void Awake()
        {
            Instance = this;

            RegisterLocalizedStrings();

            TaxRate = Config.Bind("Economy", "TaxRate", 0.02,
                "买卖印花税比例（0~0.2）。买入需要多付、卖出会少得。");
            BasePrice = Config.Bind("Economy", "BasePrice", 1.00,
                "全局基准价：1 能量币 值多少太阳帆（未计地区与涨跌）。");
            RegionSpread = Config.Bind("Economy", "RegionSpread", 0.30,
                "星系间地区价差幅度：0.3 表示各星系价格在 0.7~1.3 倍之间浮动。");
            Volatility = Config.Bind("Economy", "Volatility", 0.15,
                "时间涨跌幅度：0.15 表示价格每 5 分钟在 ±15% 内波动。");
            PricePeriodMinutes = Config.Bind("Economy", "PricePeriodMinutes", 10,
                "价格刷新周期（分钟）。默认 10 分钟。");
            PhotonCoinCost = Config.Bind("Economy", "PhotonCoinCost", 1,
                "买入 1 枚临界光子需要多少能量币。默认 1 币 = 1 光子（暂定）。");
            UnlockTechId = Config.Bind("Unlock", "UnlockTechId", 1508,
                "解锁本建筑配方的科技 ID。1508 = 任务完成！(通关科技)；填 0 表示开局即可用。");
            EnableTint = Config.Bind("Visual", "EnableTint", true,
                "把交易所建筑染成金色，便于与木色分馏塔区分。");
            VerboseLog = Config.Bind("Debug", "VerboseLog", false,
                "输出交易明细日志。");
            RequirePower = Config.Bind("Economy", "RequirePower", true,
                "交易所是否必须通电才工作（与原版建筑一致）。开发期可临时关掉。");

            try
            {
                RegisterProtos();
            }
            catch (Exception e)
            {
                LogError("RegisterProtos failed", e);
            }

            try
            {
                Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, GUID);
            }
            catch (Exception e)
            {
                LogError("Harmony patch failed", e);
            }

            try
            {
                RegisterKeyBind();
            }
            catch (Exception e)
            {
                LogError("keybind register failed", e);
            }

            try
            {
                gameObject.AddComponent<MarketOverlay>();
            }
            catch (Exception e)
            {
                LogError("MarketOverlay attach failed", e);
            }

            Logger.LogInfo($"{NAME} v{VERSION} loaded (ids: coin={ItemCoin}, building={ItemExchange})");
        }

        /// <summary>
        /// 注册界面按键（默认 Alt+3 行情面板 / Alt+4 K 线图）。CommonAPI 会把它们加进游戏「设置 → 按键」列表，
        /// 玩家可以自行改绑或清除（自定义键位会随存档保存）。
        /// </summary>
        private static void RegisterKeyBind()
        {
            ToggleMarketKey = CustomKeyBindSystem.RegisterKeyBindWithReturn<PressKeyBind>(new BuiltinKey
            {
                name = "OpenEnergyCoinMarket",
                key = new CombineKey((int)KeyCode.Alpha3, CombineKey.ALT_COMB, ECombineKeyAction.OnceClick, false),
                canOverride = true,
                conflictGroup = KeyBindConflict.MOVEMENT | KeyBindConflict.FLYING | KeyBindConflict.SAILING |
                                KeyBindConflict.BUILD_MODE_1 | KeyBindConflict.KEYBOARD_KEYBIND,
            });

            ToggleChartKey = CustomKeyBindSystem.RegisterKeyBindWithReturn<PressKeyBind>(new BuiltinKey
            {
                name = "OpenEnergyCoinChart",
                key = new CombineKey((int)KeyCode.Alpha4, CombineKey.ALT_COMB, ECombineKeyAction.OnceClick, false),
                canOverride = true,
                conflictGroup = KeyBindConflict.MOVEMENT | KeyBindConflict.FLYING | KeyBindConflict.SAILING |
                                KeyBindConflict.BUILD_MODE_1 | KeyBindConflict.KEYBOARD_KEYBIND,
            });

            // 按键列表里显示的名字；中英文各注册一份，缺省回退到 key 名。
            try
            {
                LocalizationModule.RegisterTranslation("OpenEnergyCoinMarket", "Exchange Market", "交易所行情", "Marché");
                LocalizationModule.RegisterTranslation("KEYOpenEnergyCoinMarket", "Exchange Market", "交易所行情", "Marché");
                LocalizationModule.RegisterTranslation("OpenEnergyCoinChart", "Exchange Chart", "交易所K线", "Graphique");
                LocalizationModule.RegisterTranslation("KEYOpenEnergyCoinChart", "Exchange Chart", "交易所K线", "Graphique");
            }
            catch (Exception e)
            {
                LogInfo("keybind string register skipped: " + e.Message);
            }
        }

        /// <summary>
        /// 原型名称/描述的中英文本地化：key 用中文原文，其它语言显示英文。
        /// 必须在 RegisterProtos 之前注册，否则 LDB 建原型时拿不到翻译。
        /// </summary>
        private static void RegisterLocalizedStrings()
        {
            try
            {
                LocalizationModule.RegisterTranslation("光子证券交易所", "Photon Stock Exchange", "光子证券交易所", "Bourse photonique");
                LocalizationModule.RegisterTranslation("能量币", "Energy Coin", "能量币", "Pièce d'énergie");
                LocalizationModule.RegisterTranslation("传送带式证券交易所：两侧进料、中间出料。价格随所在星系不同，并定时刷新。",
                    "Belt-fed stock exchange: the side belts feed in, the middle belt outputs. Prices differ per star system and refresh periodically.",
                    "传送带式证券交易所：两侧进料、中间出料。价格随所在星系不同，并定时刷新。",
                    "Bourse à convoyeurs : entrées latérales, sortie centrale, prix variables selon le système.");
                LocalizationModule.RegisterTranslation("建造一座光子证券交易所。", "Build a Photon Stock Exchange.",
                    "建造一座光子证券交易所。", "Construire une Bourse photonique.");
            }
            catch (Exception e)
            {
                LogInfo("localized strings register skipped: " + e.Message);
            }
        }

        private static void RegisterProtos()
        {
            using (ProtoRegistry.StartModLoad(GUID))
            {
                // 能量币：成品物，堆叠 500
                CoinItem = ProtoRegistry.RegisterItem(
                    ItemCoin,
                    "能量币",
                    "星际股票交易所的通用货币。可在不同星系之间低买高卖。",
                    "Icons/ItemRecipe/assembler-4",
                    ProtoRegistry.GetGridIndex(4, 11, 10),
                    500,
                    EItemType.Material,
                    ProtoRegistry.GetDefaultIconDesc(new UnityEngine.Color(1.00f, 0.85f, 0.20f), new UnityEngine.Color(0.95f, 0.62f, 0.10f)));
                CoinItem.UnlockKey = -1; // 货币物品本身不靠配方解锁
                CoinItem.IconTag = "coin";

                // 交易所建筑：Production 类型，靠配方建造
                ExchangeItem = ProtoRegistry.RegisterItem(
                    ItemExchange,
                    "光子证券交易所",
                    "传送带式证券交易所：两侧进料、中间出料。价格随所在星系不同，并定时刷新。",
                    "Icons/ItemRecipe/fractionator",
                    ProtoRegistry.GetGridIndex(4, 10, 3),
                    20,
                    EItemType.Production,
                    ProtoRegistry.GetDefaultIconDesc(new UnityEngine.Color(1.00f, 0.78f, 0.25f), new UnityEngine.Color(0.42f, 0.30f, 0.10f)));
                ExchangeItem.IconTag = "exchange";

                ExchangeRecipe = ProtoRegistry.RegisterRecipe(
                    RecipeExchange,
                    ERecipeType.Assemble,
                    180,
                    ExchangeRecipeInputs,
                    ExchangeRecipeCounts,
                    new[] { ItemExchange },
                    new[] { 1 },
                    "建造一座光子证券交易所。",
                    UnlockTechId.Value,
                    0,
                    "光子证券交易所",
                    "");
                ExchangeRecipe.Handcraft = false;

                // 复用原版分馏塔 prefab：三个传送带口（中间出、两侧进）与 FractionatorComponent 行为均由原版提供
                ExchangeModel = ProtoRegistry.RegisterModel(
                    ModelExchange,
                    ExchangeItem,
                    "Entities/Prefabs/fractionator",
                    null,
                    new[] { 53, 11, 12, 1, 40 },
                    0);

                ExchangeItem.BuildIndex = BuildIndexExchange;
                ExchangeItem.UnlockKey = 0;

                ProtoRegistry.onLoadingFinished += OnLoadingFinished;
            }

            Registered = true;
        }

        /// <summary>
        /// 原型全部加载完毕：此时 prefabDesc / LDB 可用，可安全改图标、改模型属性与染色。
        /// </summary>
        private static void OnLoadingFinished()
        {
            try
            {
                // 图标使用注册时生成的 iconDesc（金色调色板），不依赖额外贴图文件。
                var model = ExchangeModel ?? LDB.models.Select(ModelExchange);
                var src = LDB.models.Select(VanillaModelFractionator);
                if (model != null && model.prefabDesc != null && src != null && src.prefabDesc != null)
                {
                    model.HpMax = src.HpMax;
                    model.prefabDesc.workEnergyPerTick = src.prefabDesc.workEnergyPerTick;
                    model.prefabDesc.idleEnergyPerTick = src.prefabDesc.idleEnergyPerTick;

                    // 关闭原版分馏逻辑（我方在 GameTick Postfix 里用传送带直接结算交易）
                    model.prefabDesc.fracFluidInputMax = 0;
                    model.prefabDesc.fracProductOutputMax = 0;
                    model.prefabDesc.fracFluidOutputMax = 0;

                    if (EnableTint != null && EnableTint.Value)
                    {
                        TintModel(model, new UnityEngine.Color(1.00f, 0.78f, 0.25f));
                    }
                }

                // 交易所图标：取原版分馏塔图标，按模型金色调色（若贴图可读）
                TryApplyGoldFractionatorIcon();

                // 能量币图标：内嵌 PNG -> Sprite（蓝色边 + 白色塑料币面 + 黄色闪电）
                try
                {
                    var coinSprite = LoadEmbeddedSprite(CoinIconResource);
                    if (coinSprite != null && CoinItem != null)
                    {
                        SetItemIconSprite(CoinItem, coinSprite);
                        LogInfo("coin icon replaced: " + coinSprite.rect.width + "x" + coinSprite.rect.height);
                    }
                }
                catch (Exception e)
                {
                    LogError("coin icon load failed", e);
                }

                Instance?.Logger.LogInfo("protos ready: energy-coin exchange registered");
            }
            catch (Exception e)
            {
                LogError("OnLoadingFinished failed", e);
            }
        }

        /// <summary>
        /// 把模型的第一个材质（主体）换成金色副本；玻璃/黑色材质保持原样。
        /// </summary>
        private static void TintModel(ModelProto model, UnityEngine.Color color)
        {
            var desc = model.prefabDesc;
            if (desc == null)
            {
                return;
            }

            if (desc.lodMaterials != null)
            {
                for (int lod = 0; lod < desc.lodMaterials.Length; lod++)
                {
                    var mats = desc.lodMaterials[lod];
                    if (mats == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (i != 0 || mats[i] == null)
                        {
                            continue;
                        }
                        var tinted = new UnityEngine.Material(mats[i]) { color = color };
                        desc.lodMaterials[lod][i] = tinted;
                    }
                }
            }

            if (desc.materials != null && desc.materials.Length > 0 && desc.materials[0] != null)
            {
                desc.materials[0] = new UnityEngine.Material(desc.materials[0]) { color = color };
            }
        }

        private const string CoinIconResource = "StockExchange.coin.png";

        private static UnityEngine.Sprite goldExchangeIconSprite;

        /// <summary>把原版分馏塔图标按金色模型调色，作为交易所物品图标。</summary>
        private static void TryApplyGoldFractionatorIcon()
        {
            try
            {
                var srcItem = LDB.items.Select(VanillaItemFractionator);
                var sp = srcItem != null ? srcItem.iconSprite : null;
                if (sp == null)
                {
                    LogInfo("[icon] vanilla fractionator sprite missing");
                    return;
                }
                var tex = sp.texture;
                LogInfo(string.Format("[icon] frac sprite tex={0} readable={1} size={2}x{3} rect={4}",
                    tex != null ? tex.name : "null", tex != null && tex.isReadable,
                    tex != null ? tex.width : 0, tex != null ? tex.height : 0, sp.rect));
                if (tex == null || !tex.isReadable)
                {
                    return;
                }
                int w = (int)sp.rect.width;
                int h = (int)sp.rect.height;
                var pixels = tex.GetPixels((int)sp.rect.x, (int)sp.rect.y, w, h);
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    // 用亮度作底，再按模型金色 (1.00, 0.78, 0.24) 着色，避免整张图变暗、发绿
                    float lum = p.r > p.g ? (p.r > p.b ? p.r : p.b) : (p.g > p.b ? p.g : p.b);
                    lum = Math.Min(1f, lum * 1.12f);
                    p.r = lum;
                    p.g = lum * 0.78f;
                    p.b = lum * 0.24f;
                    pixels[i] = p;
                }
                var tinted = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGBA32, false);
                tinted.SetPixels(pixels);
                tinted.Apply();
                goldExchangeIconSprite = UnityEngine.Sprite.Create(tinted, new UnityEngine.Rect(0f, 0f, w, h), new UnityEngine.Vector2(0.5f, 0.5f), 100f);
                if (ExchangeItem != null)
                {
                    SetItemIconSprite(ExchangeItem, goldExchangeIconSprite);
                }
                LogInfo("[icon] gold fractionator icon applied: " + w + "x" + h);
            }
            catch (Exception e)
            {
                LogError("fractionator icon tint failed", e);
            }
        }


        /// <summary>ItemProto.iconSprite 是只读属性，只能通过私有字段 _iconSprite 替换。</summary>
        private static void SetItemIconSprite(ItemProto item, UnityEngine.Sprite sprite)
        {
            var field = typeof(ItemProto).GetField("_iconSprite", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(item, sprite);
            }
        }

        private static UnityEngine.Sprite coinIconSprite;

        private static UnityEngine.Sprite LoadEmbeddedSprite(string resourceName)
        {
            if (coinIconSprite != null)
            {
                return coinIconSprite;
            }
            var asm = typeof(Plugin).Assembly;
            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null || stream.Length == 0)
                {
                    return null;
                }
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0)
                    {
                        break;
                    }
                    read += n;
                }
                var tex = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, false);
                if (!UnityEngine.ImageConversion.LoadImage(tex, bytes))
                {
                    return null;
                }
                tex.filterMode = UnityEngine.FilterMode.Bilinear;
                tex.wrapMode = UnityEngine.TextureWrapMode.Clamp;
                coinIconSprite = UnityEngine.Sprite.Create(
                    tex,
                    new UnityEngine.Rect(0f, 0f, tex.width, tex.height),
                    new UnityEngine.Vector2(0.5f, 0.5f),
                    100f);
                return coinIconSprite;
            }
        }

        internal static void LogError(string what, Exception e)
        {
            if (errorLogged++ > 20)
            {
                return;
            }
            Instance?.Logger.LogError($"{what}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }

        internal static void LogInfo(string msg)
        {
            if (VerboseLog != null && VerboseLog.Value)
            {
                Instance?.Logger.LogInfo(msg);
            }
        }

        /// <summary>无条件写日志（开发辅助用，不受 VerboseLog 控制）。</summary>
        internal static void LogAlways(string msg)
        {
            Instance?.Logger.LogInfo(msg);
        }
    }
}
