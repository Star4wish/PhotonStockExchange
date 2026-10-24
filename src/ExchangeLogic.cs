using System;
using HarmonyLib;
using UnityEngine;

namespace StockExchange
{
    /// <summary>交易所的三种业务模式。</summary>
    internal enum TradeMode
    {
        /// <summary>卖出能量币：侧边进能量币，中间出太阳帆。</summary>
        SellSails = 0,

        /// <summary>买入能量币：侧边进太阳帆，中间出能量币。</summary>
        BuySails = 1,

        /// <summary>买入光子：侧边进能量币，中间出临界光子（1 币 : 1 光子）。</summary>
        BuyPhotons = 2,
    }

    /// <summary>
    /// 交易所运行时逻辑：每帧（由 MarketOverlay.Update 驱动，按 gameTick 去重）结算所有“交易所”类型分馏塔。
    /// 原版分馏逻辑已被 PrefabDesc.frac*Max = 0 关闭，这里直接操作传送带。
    /// 状态全部存放在 FractionatorComponent 自带且会被存档保存的字段里：
    ///   fluidInputCount  = 待兑换的输入缓冲（买入帆时是太阳帆；卖出/买光子时是能量币）
    ///   productOutputCount = 待输出的产物缓冲（买入帆时是能量币；卖出时是太阳帆；买光子时是光子）
    ///   progress         = 卖出时“已攒下但还不够 1 张太阳帆”的千分位（0~999）
    ///   incUsed          = 模式 + 1（0 表示旧存档/未初始化，按买入帆处理）
    /// </summary>
    internal static class ExchangeLogic
    {
        private const int MaxPendingInput = 20000;
        private const int MaxOutputBuffer = 200;
        private const int MaxConvertPerTick = 4;

        /// <summary>已执行的交易 tick 数（开发排查用）。</summary>
        internal static long TicksSeen;

        private static long lastTickedGameTick = long.MinValue;

        // ===== 模式相关的小工具 =====
        // 注意：FractionatorComponent.incUsed 是 bool（原版“是否用过增产剂”标记），不能存模式；
        // 这里改用 seed（int，会被存档保存；原版分馏逻辑已被我们的 max=0 关闭，seed 不会被使用）。
        internal static int GetMode(in FractionatorComponent frac)
        {
            int v = (int)frac.seed - 1;
            if (v < 0 || v > 2)
            {
                return (int)TradeMode.BuySails;
            }
            return v;
        }

        private static int ModeInputItem(int mode)
        {
            return mode == (int)TradeMode.BuySails ? Plugin.ItemSunSail : Plugin.ItemCoin;
        }

        private static int ModeOutputItem(int mode)
        {
            if (mode == (int)TradeMode.BuySails)
            {
                return Plugin.ItemCoin;
            }
            if (mode == (int)TradeMode.SellSails)
            {
                return Plugin.ItemSunSail;
            }
            return Plugin.ItemCriticalPhoton;
        }

        internal static string ModeName(int mode)
        {
            if (mode == (int)TradeMode.SellSails)
            {
                return L10N.T("卖出能量币（币 → 太阳帆）", "Sell coins (coins -> sails)");
            }
            if (mode == (int)TradeMode.BuyPhotons)
            {
                return L10N.T("兑换光子（币 → 光子）", "Buy photons (coins -> photons)");
            }
            return L10N.T("买入能量币（太阳帆 → 币）", "Buy coins (sails -> coins)");
        }

        // ===== 对外查询（UI 用） =====
        internal static bool TryGetExchange(FactorySystem fs, int fracId, out FractionatorComponent frac)
        {
            frac = default(FractionatorComponent);
            if (fs == null || fs.factory == null || fs.fractionatorPool == null)
            {
                return false;
            }
            if (fracId <= 0 || fracId >= fs.fractionatorPool.Length)
            {
                return false;
            }
            ref FractionatorComponent f = ref fs.fractionatorPool[fracId];
            if (f.id != fracId || f.entityId <= 0)
            {
                return false;
            }
            var entityPool = fs.factory.entityPool;
            if (f.entityId >= entityPool.Length || entityPool[f.entityId].protoId != Plugin.ItemExchange)
            {
                return false;
            }
            frac = f;
            return true;
        }

        /// <summary>在游戏界面上切换某座交易所的模式；切换时把缓冲里的物品退还给玩家。</summary>
        internal static void SetMode(FactorySystem fs, int fracId, TradeMode mode, Player player)
        {
            if (!TryGetExchange(fs, fracId, out var current))
            {
                return;
            }
            if (GetMode(in current) == (int)mode)
            {
                return;
            }
            ref FractionatorComponent frac = ref fs.fractionatorPool[fracId];
            RefundPending(ref frac, player);
            frac.seed = (uint)((int)mode + 1);
            Plugin.LogInfo($"[exchange] mode -> {mode} (frac {fracId})");
        }

        /// <summary>
        /// 每帧驱动入口（由 MarketOverlay.Update 调用）。
        /// 同一 gameTick 内只结算一次，避免与 Harmony 补丁重复。
        /// </summary>
        internal static void TickAll()
        {
            if (!Plugin.Registered)
            {
                return;
            }
            var data = GameMain.data;
            if (data == null || data.factories == null)
            {
                return;
            }
            long gt = GameMain.gameTick;
            if (gt == lastTickedGameTick)
            {
                return;
            }
            lastTickedGameTick = gt;

            int count = data.factoryCount;
            if (count > data.factories.Length)
            {
                count = data.factories.Length;
            }
            for (int i = 0; i < count; i++)
            {
                var factory = data.factories[i];
                if (factory == null || factory.factorySystem == null)
                {
                    continue;
                }
                try
                {
                    TickFactory(factory.factorySystem);
                }
                catch (Exception e)
                {
                    Plugin.LogError("TickAll factory failed", e);
                }
            }
        }

        internal static void TickFactory(FactorySystem fs)
        {
            if (!Plugin.Registered || fs == null)
            {
                return;
            }
            TicksSeen++;
            if (GameMain.data == null || GameMain.history == null)
            {
                return;
            }

            EnsureRecipeUnlocked();

            var factory = fs.factory;
            if (factory == null)
            {
                return;
            }
            var traffic = factory.cargoTraffic;
            if (traffic == null)
            {
                return;
            }
            var fracPool = fs.fractionatorPool;
            if (fracPool == null)
            {
                return;
            }

            int cursor = fs.fractionatorCursor;
            if (cursor > fracPool.Length)
            {
                cursor = fracPool.Length;
            }
            var entityPool = factory.entityPool;

            for (int i = 1; i < cursor; i++)
            {
                ref FractionatorComponent frac = ref fracPool[i];
                if (frac.id != i)
                {
                    continue;
                }
                int eid = frac.entityId;
                if (eid <= 0 || eid >= entityPool.Length)
                {
                    continue;
                }
                if (entityPool[eid].protoId != Plugin.ItemExchange)
                {
                    continue;
                }
                try
                {
                    Process(ref frac, fs, traffic);
                }
                catch (Exception e)
                {
                    Plugin.LogError("trade tick failed (fracId=" + i + ")", e);
                }
            }
        }

        /// <summary>
        /// 通关科技 1508 解锁时，把交易所配方补进历史（存档里的 recipeUnlocked 不会自动包含新配方）。
        /// </summary>
        internal static void EnsureRecipeUnlocked()
        {
            var history = GameMain.history;
            if (history == null)
            {
                return;
            }
            int techId = Plugin.UnlockTechId.Value;
            if (techId <= 0)
            {
                if (!history.RecipeUnlocked(Plugin.RecipeExchange))
                {
                    history.UnlockRecipe(Plugin.RecipeExchange);
                }
                return;
            }
            if (!history.RecipeUnlocked(Plugin.RecipeExchange) && history.TechUnlocked(techId))
            {
                history.UnlockRecipe(Plugin.RecipeExchange);
            }
        }

        private static void Process(ref FractionatorComponent frac, FactorySystem fs, CargoTraffic traffic)
        {
            // 抑制原版分馏逻辑（每帧兜底一次，防止旧存档/旧建筑残留容量）
            frac.fluidInputMax = 0;
            frac.productOutputMax = 0;
            frac.fluidOutputMax = 0;

            var star = fs.planet != null ? fs.planet.star : null;
            if (star == null)
            {
                return;
            }
            int mode = GetMode(in frac);
            // 旧存档/新建筑：把模式写回存档字段，保证下次读档一致
            if (frac.seed != (uint)(mode + 1))
            {
                frac.seed = (uint)(mode + 1);
            }

            double price = PriceService.GetPrice(star);
            double tax = Mathf.Clamp((float)Plugin.TaxRate.Value, 0f, 0.5f);

            // 两侧收料：只收当前模式需要的物品，避免混料
            int inputItem = ModeInputItem(mode);
            if (!frac.isOutput1 && frac.belt1 > 0)
            {
                IntakeFromBelt(ref frac, traffic, frac.belt1, inputItem);
            }
            if (!frac.isOutput2 && frac.belt2 > 0)
            {
                IntakeFromBelt(ref frac, traffic, frac.belt2, inputItem);
            }

            // 供电判定：与原版建筑一致，没电不结算（缓冲保留）
            float power = GetPowerRatio(fs, in frac);
            bool powered = Plugin.RequirePower == null || !Plugin.RequirePower.Value || power > 0.1f;

            // 结算
            if (powered)
            {
                if (mode == (int)TradeMode.BuySails)
                {
                    ConvertBuySails(ref frac, price, tax);
                }
                else if (mode == (int)TradeMode.SellSails)
                {
                    ConvertSellSails(ref frac, price, tax);
                }
                else
                {
                    ConvertBuyPhotons(ref frac);
                }

                // 中间出料
                OutputToBelt(ref frac, traffic, ModeOutputItem(mode));
            }

            frac.isWorking = powered && (frac.fluidInputCount > 0 || frac.productOutputCount > 0 ||
                             frac.fluidInputInc > 0 || frac.fluidOutputCount > 0 || frac.progress > 0);
        }

        /// <summary>取该建筑所在电网的供电比例（0..1，无电网为 0）。</summary>
        internal static float GetPowerRatio(FactorySystem fs, in FractionatorComponent frac)
        {
            try
            {
                var pf = fs.factory;
                var ps = pf != null ? pf.powerSystem : null;
                if (ps == null || frac.pcId <= 0 || frac.pcId >= ps.consumerPool.Length)
                {
                    return 0f;
                }
                int netId = ps.consumerPool[frac.pcId].networkId;
                if (netId <= 0 || ps.netPool == null || netId >= ps.netPool.Length)
                {
                    return 0f;
                }
                var net = ps.netPool[netId];
                return net != null ? (float)net.consumerRatio : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>从侧边传送带尾部取指定物品，放入输入缓冲。</summary>
        private static void IntakeFromBelt(ref FractionatorComponent frac, CargoTraffic traffic, int beltId, int itemId)
        {
            if (frac.fluidInputCount >= MaxPendingInput)
            {
                return;
            }
            int got = traffic.GetItemIdAtRear(beltId);
            if (got != itemId)
            {
                return;
            }
            int picked = traffic.TryPickItemAtRear(beltId, itemId, null, out byte stack, out byte inc);
            if (picked > 0 && stack > 0)
            {
                frac.fluidInputCount += stack;
                frac.fluidInputCargoCount = stack;
                Plugin.LogInfo($"in : +{stack} #{itemId} (pending {frac.fluidInputCount})");
            }
        }

        /// <summary>买入方向（太阳帆 -> 能量币）：每 ceil(price*(1+tax)) 张帆换 1 枚币。</summary>
        private static void ConvertBuySails(ref FractionatorComponent frac, double price, double tax)
        {
            int need = Math.Max(1, (int)Math.Ceiling(price * (1.0 + tax)));
            int guard = 0;
            while (frac.fluidInputCount >= need && frac.productOutputCount < MaxOutputBuffer && guard++ < MaxConvertPerTick)
            {
                frac.fluidInputCount -= need;
                frac.productOutputCount++;
                Plugin.LogInfo($"buy: -{need} sail -> +1 coin (out buffer {frac.productOutputCount})");
            }
        }

        /// <summary>卖出方向（能量币 -> 太阳帆）：每枚币按 price*(1-tax) 累积千分位，攒够 1 张帆。</summary>
        private static void ConvertSellSails(ref FractionatorComponent frac, double price, double tax)
        {
            double sailsPerCoin = price * (1.0 - tax);
            int addMilli = (int)Math.Floor(sailsPerCoin * 1000.0);
            if (addMilli <= 0 && sailsPerCoin > 0.0)
            {
                addMilli = 1;
            }
            int guard = 0;
            while (frac.fluidInputCount > 0 && frac.progress < 100000 && guard++ < MaxConvertPerTick)
            {
                frac.fluidInputCount--;
                frac.progress += addMilli;
            }
            while (frac.progress >= 1000 && frac.productOutputCount < MaxOutputBuffer)
            {
                frac.progress -= 1000;
                frac.productOutputCount++;
                Plugin.LogInfo($"sell: +1 sail (out buffer {frac.productOutputCount}, milli left {frac.progress})");
            }
        }

        /// <summary>买入光子（能量币 -> 临界光子）：默认 1 币 : 1 光子，不收印花税。</summary>
        private static void ConvertBuyPhotons(ref FractionatorComponent frac)
        {
            int cost = Math.Max(1, Plugin.PhotonCoinCost.Value);
            int guard = 0;
            while (frac.fluidInputCount >= cost && frac.productOutputCount < MaxOutputBuffer && guard++ < MaxConvertPerTick)
            {
                frac.fluidInputCount -= cost;
                frac.productOutputCount++;
                Plugin.LogInfo($"photon: -{cost} coin -> +1 photon (out buffer {frac.productOutputCount})");
            }
        }

        /// <summary>从中间口（belt0）输出产物，每 tick 最多 1 件。</summary>
        private static void OutputToBelt(ref FractionatorComponent frac, CargoTraffic traffic, int outputItem)
        {
            int beltId = frac.belt0;
            if (beltId <= 0)
            {
                return;
            }
            if (frac.productOutputCount > 0 && traffic.TryInsertItemAtHead(beltId, outputItem, 1, 0))
            {
                frac.productOutputCount--;
            }
        }

        /// <summary>拆除交易所 / 切换模式时返还缓冲中的物品。</summary>
        internal static void RefundPending(ref FractionatorComponent frac, Player player)
        {
            int mode = GetMode(in frac);
            int refundInput = frac.fluidInputCount;
            int refundOutput = frac.productOutputCount;
            int refundSailsFromProgress = mode == (int)TradeMode.SellSails ? frac.progress / 1000 : 0;
            int refundOldCoin = frac.fluidInputInc;
            int refundOldSail = frac.fluidOutputCount;
            Plugin.LogInfo($"[refund] frac entity={frac.entityId} mode={mode} in={refundInput} out={refundOutput} prog={frac.progress} oldCoin={refundOldCoin} oldSail={refundOldSail} player={(player != null)}");
            if (player != null)
            {
                int inputItem = ModeInputItem(mode);
                int outputItem = ModeOutputItem(mode);
                if (refundInput > 0)
                {
                    player.TryAddItemToPackage(inputItem, refundInput, 0, true);
                }
                if (refundOutput > 0)
                {
                    player.TryAddItemToPackage(outputItem, refundOutput, 0, true);
                }
                if (refundSailsFromProgress > 0)
                {
                    player.TryAddItemToPackage(Plugin.ItemSunSail, refundSailsFromProgress, 0, true);
                }
                // 兼容旧版本缓冲字段
                if (refundOldCoin > 0)
                {
                    player.TryAddItemToPackage(Plugin.ItemCoin, refundOldCoin, 0, true);
                }
                if (refundOldSail > 0)
                {
                    player.TryAddItemToPackage(Plugin.ItemSunSail, refundOldSail, 0, true);
                }
            }
            frac.fluidInputCount = 0;
            frac.fluidInputInc = 0;
            frac.fluidOutputCount = 0;
            frac.fluidOutputInc = 0;
            frac.productOutputCount = 0;
            frac.progress = 0;
        }
    }

    /// <summary>确定性价格：星系固有地区价差 × 每 5 分钟一跳的时间波动。</summary>
    public static class PriceService
    {
        public static long GetEpoch()
        {
            double tps = GameMain.tickPerSec > 0.0 ? GameMain.tickPerSec : 60.0;
            int minutes = Math.Max(1, Plugin.PricePeriodMinutes.Value);
            long period = (long)(tps * 60.0 * minutes);
            return period <= 0 ? 0 : GameMain.gameTick / period;
        }

        public static double GetPrice(StarData star)
        {
            return GetPriceAt(star, GetEpoch());
        }

        public static double GetPriceAt(StarData star, long epoch)
        {
            return GetPriceAtContinuous(star, epoch);
        }

        /// <summary>连续版本（用于 K 线在一个周期内采样开/高/低/收）。</summary>
        public static double GetPriceAtContinuous(StarData star, double epoch)
        {
            if (star == null)
            {
                return Math.Max(0.1, Plugin.BasePrice.Value);
            }
            double basePrice = Plugin.BasePrice.Value;
            double spread = Plugin.RegionSpread.Value;
            double vol = Plugin.Volatility.Value;

            // 地区价差（按星系种子与索引确定）
            double regionUnit = Hash01(star.seed, star.index * 31 + 7);          // [0,1)
            double region = 1.0 + spread * (regionUnit * 2.0 - 1.0);            // 1±spread

            // 时间波动：两个不同频率的正弦叠加，每个刷新周期一跳
            double phase = Hash01(star.seed * 17 + 3, star.index * 131 + 11) * Math.PI * 2.0;
            double wave = 0.65 * Math.Sin(epoch * 0.90 + phase) +
                          0.35 * Math.Sin(epoch * 0.37 + phase * 2.1);

            double p = basePrice * region * (1.0 + vol * wave);
            if (p < 0.2)
            {
                p = 0.2;
            }
            else if (p > 5.0)
            {
                p = 5.0;
            }
            return p;
        }

        /// <summary>税后报价：买入（帆->币）需要多少帆；卖出（币->帆）得到多少帆。</summary>
        public static void GetQuotes(double price, out int sailsPerCoin, out double sailsPerCoinSold)
        {
            double tax = Math.Max(0.0, Math.Min(0.5, Plugin.TaxRate.Value));
            sailsPerCoin = Math.Max(1, (int)Math.Ceiling(price * (1.0 + tax)));
            sailsPerCoinSold = Math.Max(0.0, price * (1.0 - tax));
        }

        private static double Hash01(int a, int b)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                h = (h ^ (uint)a) * 1099511628211UL;
                h = (h ^ (uint)b) * 1099511628211UL;
                h = (h ^ (h >> 33)) * 0xff51afd7ed558ccdUL;
                h = (h ^ (h >> 33)) * 0xc4ceb9fe1a85ec53UL;
                h ^= h >> 33;
                return (h % 1000000UL) / 1000000.0;
            }
        }
    }
}
