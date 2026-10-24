using System;
using System.Collections.Generic;
using UnityEngine;

namespace StockExchange
{
    /// <summary>
    /// 界面层（IMGUI）：
    ///   1) 行情面板（默认 Alt+3，可在游戏按键设置里改绑）：当前星系税后报价 + 全星系最便宜/最贵排行（显示星系名前 6 字）；
    ///   2) K 线图（默认 Alt+4）：当前星系能量币的蜡烛图（开/高/低/收），显示在右上角；
    ///   3) 打开交易所窗口时，在旁边显示该建筑的当前模式与三个模式按钮，右上角红 X 关闭窗口。
    /// </summary>
    internal class MarketOverlay : MonoBehaviour
    {
        private static bool marketVisible;
        private static bool chartVisible;
        private static bool stylesReady;
        private static Font cjkFont;

        private static GUIStyle panelStyle;
        private static GUIStyle titleStyle;
        private static GUIStyle textStyle;
        private static GUIStyle hintStyle;
        private static GUIStyle buttonStyle;
        private static GUIStyle goldStyle;
        private static GUIStyle closeStyle;
        private static GUIStyle closeLabelStyle;
        private static Texture2D closeTex;
        private static GUIStyle rowStyle;

        private static readonly List<StarData> cheapest = new List<StarData>();
        private static readonly List<StarData> dearest = new List<StarData>();
        private static readonly List<double> cheapestPrice = new List<double>();
        private static readonly List<double> dearestPrice = new List<double>();
        private static readonly List<Rect> exchangeButtonRects = new List<Rect>();
        private static readonly List<TradeMode> exchangeButtonModes = new List<TradeMode>();
        private static readonly List<Rect> marketStarRects = new List<Rect>();
        private static readonly List<StarData> marketStarTargets = new List<StarData>();
        private static long cachedEpoch = long.MinValue;

        // 需要手工命中的按钮（DSP 的 IMGUI 收不到鼠标事件）
        private static Rect marketCloseRect;
        private static Rect chartCloseRect;
        private static Rect exchangeCloseRect;
        private static bool hasMarketClose;
        private static bool hasChartClose;
        private static bool hasExchangeClose;

        private static Texture2D whiteTex;

        // ================= 生命周期 =================

        private void Update()
        {
            try
            {
                if (GameMain.data != null)
                {
                    ExchangeLogic.TickAll();
                }
                HandleClicks();

                if (Plugin.ToggleMarketKey != null && Plugin.ToggleMarketKey.keyValue)
                {
                    marketVisible = !marketVisible;
                    if (marketVisible)
                    {
                        chartVisible = false;
                    }
                }
                if (Plugin.ToggleChartKey != null && Plugin.ToggleChartKey.keyValue)
                {
                    chartVisible = !chartVisible;
                    if (chartVisible)
                    {
                        marketVisible = false;
                    }
                }

                if (marketVisible && Input.GetKeyDown(KeyCode.Escape))
                {
                    marketVisible = false;
                }
                if (chartVisible && Input.GetKeyDown(KeyCode.Escape))
                {
                    chartVisible = false;
                }

            }
            catch (Exception e)
            {
                Plugin.LogError("overlay input failed", e);
            }
        }


        private void OnGUI()
        {
            try
            {
                EnsureStyles();

                hasMarketClose = false;
                hasChartClose = false;
                hasExchangeClose = false;
                exchangeButtonRects.Clear();
                exchangeButtonModes.Clear();
                marketStarRects.Clear();
                marketStarTargets.Clear();

                if (marketVisible)
                {
                    DrawMarketPanel();
                }
                if (chartVisible)
                {
                    DrawChartPanel();
                }
                DrawOpenExchangePanel();
            }
            catch (Exception e)
            {
                Plugin.LogError("overlay gui failed", e);
            }
        }

        // ================= 点击命中 =================

        private static void HandleClicks()
        {
            if (!Input.GetMouseButtonDown(0))
            {
                return;
            }
            var mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);

            if (hasMarketClose && marketCloseRect.Contains(mp))
            {
                marketVisible = false;
                return;
            }
            if (hasChartClose && chartCloseRect.Contains(mp))
            {
                chartVisible = false;
                return;
            }
            if (hasExchangeClose && exchangeCloseRect.Contains(mp))
            {
                var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
                if (uiGame != null)
                {
                    uiGame.ShutFractionatorWindow();
                }
                return;
            }

            for (int i = 0; i < exchangeButtonRects.Count; i++)
            {
                if (!exchangeButtonRects[i].Contains(mp))
                {
                    continue;
                }
                var win = GetOpenFractionatorWindow();
                if (win == null)
                {
                    return;
                }
                if (!ExchangeLogic.TryGetExchange(win.factorySystem, win.fractionatorId, out _))
                {
                    return;
                }
                Plugin.LogInfo("[ui] click mode button " + exchangeButtonModes[i]);
                ExchangeLogic.SetMode(win.factorySystem, win.fractionatorId, exchangeButtonModes[i], GameMain.mainPlayer);
                return;
            }

            if (marketVisible)
            {
                for (int i = 0; i < marketStarRects.Count && i < marketStarTargets.Count; i++)
                {
                    if (!marketStarRects[i].Contains(mp))
                    {
                        continue;
                    }
                    ToggleNavigation(marketStarTargets[i]);
                    return;
                }
            }
        }

        /// <summary>
        /// 点击排行里的星系名：在星图上给该星系添加/取消导航标记；
        /// 与原版星图「导航」按钮语义一致（同一目标再点一次则清除）。
        /// </summary>
        private static void ToggleNavigation(StarData star)
        {
            try
            {
                var player = GameMain.mainPlayer;
                if (player == null || player.navigation == null || star == null)
                {
                    return;
                }
                int astroId = star.astroId;
                if (astroId <= 0)
                {
                    return;
                }
                player.navigation.indicatorAstroId = player.navigation.indicatorAstroId == astroId ? 0 : astroId;
                Plugin.LogInfo("navigate star " + star.displayName + " -> " + astroId);
            }
            catch (Exception e)
            {
                Plugin.LogError("toggle navigation failed", e);
            }
        }

        // ================= 行情面板 =================

        private void DrawMarketPanel()
        {
            RefreshMarketCache();

            var star = GameMain.localStar;
            if (star == null)
            {
                return;
            }

            double epoch = PriceService.GetEpoch();
            double price = PriceService.GetPriceAt(star, (long)epoch);
            double prev = PriceService.GetPriceAt(star, (long)epoch - 1);
            double change = prev > 0.0001 ? (price - prev) / prev * 100.0 : 0.0;
            double tax = Plugin.TaxRate.Value;
            PriceService.GetQuotes(price, out int sailsPerCoin, out double sailsPerCoinSold);

            const float x = 16f;
            const float y = 16f;
            const float w = 560f;
            const float h = 382f;
            GUI.Box(new Rect(x, y, w, h), GUIContent.none, panelStyle);

            // 标题 + 右上角红 X
            GUI.Label(new Rect(x + 16f, y + 8f, w - 60f, 24f), L10N.T("光子证券交易所 · 行情", "Photon Stock Exchange · Market"), titleStyle);
            DrawCloseButton(x + w - 30f, y + 10f, out marketCloseRect, out hasMarketClose);

            float ty = y + 38f;
            GUI.Label(new Rect(x + 16f, ty, w - 32f, 20f),
                string.Format(L10N.T("当前星系：{0}", "Current system: {0}"), ShortName(star)), textStyle);
            ty += 21f;
            GUI.Label(new Rect(x + 16f, ty, w - 32f, 20f),
                string.Format(L10N.T("基准价 1 币 = {0:0.00} 帆 ({1:+0.0;-0.0;0.0}%)", "Base price 1 coin = {0:0.00} sails ({1:+0.0;-0.0;0.0}%)"), price, change), textStyle);
            ty += 21f;
            GUI.Label(new Rect(x + 16f, ty, w - 32f, 20f),
                string.Format(L10N.T("税后 买币 {0} 帆 = 1 币 ｜ 卖币 1 币 = {1:0.000} 帆",
                        "After tax  buy {0} sails = 1 coin  |  sell 1 coin = {1:0.000} sails"),
                    sailsPerCoin, sailsPerCoinSold), goldStyle);
            ty += 21f;
            GUI.Label(new Rect(x + 16f, ty, w - 32f, 20f),
                L10N.T("兑换光子 1 币 = 1 光子（不计税）", "Photon exchange: 1 coin = 1 photon (tax free)"), goldStyle);
            ty += 22f;
            GUI.Label(new Rect(x + 16f, ty, w - 32f, 18f),
                string.Format(L10N.T("第 {0} 期 ｜ 每 {1} 分钟刷新 ｜ 税率 {2:0.0}%",
                        "Epoch {0}  |  refresh every {1} min  |  tax {2:0.0}%"), (long)epoch, Plugin.PricePeriodMinutes.Value, tax * 100.0),
                hintStyle);
            ty += 26f;
            GUI.Label(new Rect(x + 16f, ty, w - 32f, 18f), L10N.T("最便宜星系（适合买入能量币，然后运到贵星系卖出）", "Cheapest systems (buy coins here, sell elsewhere)"), hintStyle);
            ty += 22f;
            DrawRanking(x, ty, w, cheapest, cheapestPrice);
            ty += 78f;

            GUI.Label(new Rect(x + 16f, ty, w - 32f, 18f), L10N.T("最贵星系（适合卖出能量币）", "Dearest systems (sell coins here)"), hintStyle);
            ty += 22f;
            DrawRanking(x, ty, w, dearest, dearestPrice);

            GUI.Label(new Rect(x + 16f, y + h - 26f, w - 32f, 18f),
                L10N.T("点击星系名称可在星图上添加/取消导航线", "Click a system name to toggle its navigation marker"), hintStyle);
        }

        /// <summary>排行：4 × 2 列，显示星系名前 6 个字 + 价格。</summary>
        private static void DrawRanking(float x, float ty, float w, List<StarData> stars, List<double> prices)
        {
            const float rowH = 22f;
            float colW = (w - 40f) * 0.5f;
            for (int i = 0; i < stars.Count && i < 6; i++)
            {
                int col = i / 3;
                int row = i % 3;
                float rx = x + 18f + col * colW;
                float ry = ty + row * rowH;
                GUI.Label(new Rect(rx, ry, colW - 8f, rowH),
                    string.Format("{0}. {1}", i + 1, ShortName(stars[i])), rowStyle);
                GUI.Label(new Rect(rx + colW - 92f, ry, 60f, rowH),
                    prices[i].ToString("0.00"), textStyle);
                marketStarRects.Add(new Rect(rx, ry, colW - 8f, rowH));
                marketStarTargets.Add(stars[i]);
            }
        }

        // ================= K 线图 =================

        private void DrawChartPanel()
        {
            var star = GameMain.localStar;
            if (star == null)
            {
                return;
            }

            const float w = 372f;
            const float h = 236f;
            float x = Screen.width - w - 16f;
            const float y = 16f;
            GUI.Box(new Rect(x, y, w, h), GUIContent.none, panelStyle);

            GUI.Label(new Rect(x + 14f, y + 8f, w - 60f, 22f), L10N.T("光子证券交易所 · K 线 · ", "Photon Exchange · Chart · ") + ShortName(star), titleStyle);
            DrawCloseButton(x + w - 30f, y + 9f, out chartCloseRect, out hasChartClose);

            long epochNow = PriceService.GetEpoch();
            double priceNow = PriceService.GetPriceAtContinuous(star, epochNow);
            const int periods = 36;
            var open = new double[periods];
            var close = new double[periods];
            var high = new double[periods];
            var low = new double[periods];
            double min = double.MaxValue;
            double max = double.MinValue;
            for (int i = 0; i < periods; i++)
            {
                double baseEpoch = epochNow - (periods - 1 - i);
                open[i] = PriceService.GetPriceAtContinuous(star, baseEpoch);
                close[i] = PriceService.GetPriceAtContinuous(star, baseEpoch + 0.999);
                high[i] = Math.Max(open[i], close[i]);
                low[i] = Math.Min(open[i], close[i]);
                for (int k = 1; k < 8; k++)
                {
                    double p = PriceService.GetPriceAtContinuous(star, baseEpoch + k / 8.0);
                    if (p > high[i]) high[i] = p;
                    if (p < low[i]) low[i] = p;
                }
                if (low[i] < min) min = low[i];
                if (high[i] > max) max = high[i];
            }

            double pad = Math.Max(0.02, (max - min) * 0.08);
            min -= pad;
            max += pad;

            const float plotX = 40f;   // 相对 panel
            const float plotY = 56f;
            const float plotW = w - 56f;
            const float plotH = h - 92f;
            float px = x + plotX;
            float py = y + plotY;

            // 背景网格 + 价格刻度
            var gridColor = new Color(1f, 1f, 1f, 0.08f);
            for (int g = 0; g <= 3; g++)
            {
                float gy = py + plotH * g / 3f;
                DrawRect(new Rect(px, gy, plotW, 1f), gridColor);
                double v = max - (max - min) * g / 3.0;
                GUI.Label(new Rect(x + 4f, gy - 8f, plotX - 6f, 16f), v.ToString("0.00"), hintStyle);
            }

            float step = plotW / periods;
            float bodyW = Math.Max(3f, step * 0.55f);
            for (int i = 0; i < periods; i++)
            {
                float cx = px + i * step + step * 0.5f;
                bool up = close[i] >= open[i];
                var color = up ? new Color(0.95f, 0.30f, 0.28f) : new Color(0.30f, 0.85f, 0.45f);

                float yHigh = PriceToY(high[i], min, max, py, plotH);
                float yLow = PriceToY(low[i], min, max, py, plotH);
                DrawRect(new Rect(cx - 0.5f, yHigh, 1.5f, Math.Max(1f, yLow - yHigh)), color);

                float yOpen = PriceToY(open[i], min, max, py, plotH);
                float yClose = PriceToY(close[i], min, max, py, plotH);
                float top = Math.Min(yOpen, yClose);
                float bh = Math.Max(1.5f, Math.Abs(yClose - yOpen));
                DrawRect(new Rect(cx - bodyW * 0.5f, top, bodyW, bh), color);
            }

            // 现价虚线
            float yNow = PriceToY(priceNow, min, max, py, plotH);
            DrawRect(new Rect(px, yNow, plotW, 1f), new Color(1f, 0.85f, 0.3f, 0.55f));

            GUI.Label(new Rect(x + 14f, y + h - 26f, w - 28f, 18f),
                string.Format(L10N.T("现价 {0:0.00} ｜ 每格 {1} 分钟 ｜ 红涨绿跌 ｜ 共 {2} 格",
                        "Now {0:0.00} | {1} min/bar | red up, green down | {2} bars"),
                    priceNow, Math.Max(1, Plugin.PricePeriodMinutes.Value), periods),
                hintStyle);
        }

        private static float PriceToY(double price, double min, double max, float py, float plotH)
        {
            if (max - min < 1e-9)
            {
                return py + plotH * 0.5f;
            }
            double t = (price - min) / (max - min);
            return (float)(py + plotH * (1.0 - t));
        }

        // ================= 交易所建筑面板 =================

        private void DrawOpenExchangePanel()
        {
            var window = GetOpenFractionatorWindow();
            if (window == null)
            {
                return;
            }
            if (!ExchangeLogic.TryGetExchange(window.factorySystem, window.fractionatorId, out var frac))
            {
                return;
            }

            var fs = window.factorySystem;
            var star = fs.planet != null ? fs.planet.star : null;
            if (star == null)
            {
                return;
            }

            int fracId = window.fractionatorId;
            int mode = ExchangeLogic.GetMode(in frac);
            double price = PriceService.GetPrice(star);
            PriceService.GetQuotes(price, out int sailsPerCoin, out double sailsPerCoinSold);
            int photonCost = Math.Max(1, Plugin.PhotonCoinCost.Value);
            int pending = frac.fluidInputCount;
            int buffered = frac.productOutputCount;
            float power = ExchangeLogic.GetPowerRatio(fs, in frac);

            string status = power <= 0.1f ? L10N.T("未供电", "No power") : (power < 0.99f ? L10N.T("电力不足", "Low power") : L10N.T("已通电", "Powered"));

            const float w = 620f;
            float x = 16f;
            float y = marketVisible ? 404f : 16f;
            const float h = 344f;
            GUI.Box(new Rect(x, y, w, h), GUIContent.none, panelStyle);

            float tx = x + 14f;
            float ty = y + 8f;
            GUI.Label(new Rect(tx, ty, w - 64f, 24f),
                string.Format(L10N.T("光子证券交易所 #{0} · {1}", "Photon Stock Exchange #{0} · {1}"), fracId, ShortName(star)), titleStyle);
            DrawCloseButton(x + w - 34f, y + 8f, out exchangeCloseRect, out hasExchangeClose);
            ty += 30f;
            GUI.Label(new Rect(tx, ty, w - 30f, 24f), string.Format(L10N.T("状态：{0}    模式：{1}", "Status: {0}    Mode: {1}"), status, ExchangeLogic.ModeName(mode)), goldStyle);
            ty += 27f;
            GUI.Label(new Rect(tx, ty, w - 30f, 22f), string.Format(L10N.T("基准价：1 能量币 = {0:0.00} 太阳帆", "Base price: 1 coin = {0:0.00} sails"), price), textStyle);
            ty += 26f;
            GUI.Label(new Rect(tx, ty, w - 30f, 22f), string.Format(L10N.T("税后：买币 {0} 帆 = 1 币", "After tax: buy {0} sails = 1 coin"), sailsPerCoin), textStyle);
            ty += 26f;
            GUI.Label(new Rect(tx, ty, w - 30f, 22f), string.Format(L10N.T("税后：卖币 1 币 = {0:0.000} 帆", "After tax: sell 1 coin = {0:0.000} sails"), sailsPerCoinSold), textStyle);
            ty += 26f;
            GUI.Label(new Rect(tx, ty, w - 30f, 22f), string.Format(L10N.T("兑换光子：{0} 能量币 = 1 光子（不计税）", "Photons: {0} coins = 1 photon (tax free)"), photonCost), textStyle);
            ty += 26f;
            GUI.Label(new Rect(tx, ty, w - 30f, 22f),
                string.Format(L10N.T("仓内：输入 {0} 件 ｜ 产出 {1} 件", "Buffered: input {0}  |  output {1}"), pending, buffered), hintStyle);
            ty += 30f;
            GUI.Label(new Rect(tx, ty, w - 30f, 22f), L10N.T("切换模式（仓内物品会退还给你）：", "Switch mode (buffered items are refunded to you):"), hintStyle);
            ty += 26f;

            DrawModeButton(new Rect(tx, ty, 168f, 28f), L10N.T("卖出币（币→帆）", "Sell coins (coins->sails)"), TradeMode.SellSails, mode);
            DrawModeButton(new Rect(tx + 176f, ty, 168f, 28f), L10N.T("买入币（帆→币）", "Buy coins (sails->coins)"), TradeMode.BuySails, mode);
            DrawModeButton(new Rect(tx + 352f, ty, 196f, 28f), L10N.T("兑换光子（币→光子）", "Buy photons (coins->photons)"), TradeMode.BuyPhotons, mode);
            ty += 34f;
            GUI.Label(new Rect(tx, ty, w - 30f, 22f), L10N.T("两侧传送带进货，中间传送带出货（无延迟）。", "Side belts = input, middle belt = output (no delay)."), hintStyle);
        }

        private static void DrawModeButton(Rect rect, string label, TradeMode target, int currentMode)
        {
            bool active = (int)target == currentMode;
            string text = active ? "> " + label + " <" : label;
            GUI.Button(rect, text, buttonStyle);
            exchangeButtonRects.Add(rect);
            exchangeButtonModes.Add(target);
        }

        private static UIFractionatorWindow GetOpenFractionatorWindow()
        {
            try
            {
                var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
                if (uiGame == null)
                {
                    return null;
                }
                var window = uiGame.fractionatorWindow;
                if (window == null || !window.active || window.fractionatorId <= 0 || window.factorySystem == null)
                {
                    return null;
                }
                return window;
            }
            catch
            {
                return null;
            }
        }

        // ================= 行情缓存 =================

        private static string ShortName(StarData star)
        {
            if (star == null)
            {
                return "?";
            }
            string name = star.displayName;
            if (string.IsNullOrEmpty(name))
            {
                name = star.name;
            }
            if (string.IsNullOrEmpty(name))
            {
                return "#" + star.index;
            }
            // 星系名可能很长，只显示前 6 个字（用户要求）
            return name.Length <= 6 ? name : name.Substring(0, 6);
        }

        private static void RefreshMarketCache()
        {
            long epoch = PriceService.GetEpoch();
            if (epoch == cachedEpoch && cheapest.Count > 0)
            {
                return;
            }
            cachedEpoch = epoch;

            cheapest.Clear();
            dearest.Clear();
            cheapestPrice.Clear();
            dearestPrice.Clear();

            var galaxy = GameMain.galaxy;
            if (galaxy == null || galaxy.stars == null)
            {
                return;
            }

            int count = galaxy.starCount;
            for (int i = 0; i < count; i++)
            {
                var s = galaxy.stars[i];
                if (s == null)
                {
                    continue;
                }
                double p = PriceService.GetPriceAt(s, epoch);
                InsertSorted(s, p, cheapest, cheapestPrice, true);
                InsertSorted(s, p, dearest, dearestPrice, false);
            }
        }

        private static void InsertSorted(StarData star, double price, List<StarData> stars, List<double> prices, bool ascending)
        {
            int pos = prices.Count;
            int limit = 6;
            for (int i = 0; i < prices.Count; i++)
            {
                bool better = ascending ? price < prices[i] : price > prices[i];
                if (better)
                {
                    pos = i;
                    break;
                }
            }
            if (pos >= limit)
            {
                return;
            }
            prices.Insert(pos, price);
            stars.Insert(pos, star);
            if (prices.Count > limit)
            {
                prices.RemoveAt(prices.Count - 1);
                stars.RemoveAt(stars.Count - 1);
            }
        }

        // ================= 样式与绘制 =================

        private static void DrawCloseButton(float x, float y, out Rect rect, out bool has)
        {
            rect = new Rect(x, y, 22f, 22f);
            has = true;
            GUI.Box(rect, GUIContent.none, closeStyle);
            if (closeTex == null)
            {
                closeTex = MakeCloseTexture();
            }
            GUI.DrawTexture(new Rect(rect.x + 5f, rect.y + 5f, 12f, 12f), closeTex);
        }

        /// <summary>画一个像素级居中的红叉（默认字体的 × 字形在字框里偏上，直接画纹理更稳）。</summary>
        private static Texture2D MakeCloseTexture()
        {
            const int size = 12;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var clear = new Color(1f, 1f, 1f, 0f);
            var red = new Color(1f, 0.38f, 0.33f, 1f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool onMain = Mathf.Abs(x - y) <= 1;
                    bool onAnti = Mathf.Abs(x - (size - 1 - y)) <= 1;
                    bool corner = (x < 2 && y < 2) || (x < 2 && y > size - 3) || (x > size - 3 && y < 2) || (x > size - 3 && y > size - 3);
                    tex.SetPixel(x, y, (onMain || onAnti) && !corner ? red : clear);
                }
            }
            tex.Apply();
            return tex;
        }

        private static void DrawRect(Rect rect, Color color)
        {
            if (whiteTex == null)
            {
                whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                whiteTex.SetPixel(0, 0, Color.white);
                whiteTex.Apply();
            }
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, whiteTex);
            GUI.color = old;
        }

        private static Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private static void EnsureStyles()
        {
            if (stylesReady)
            {
                return;
            }
            stylesReady = true;

            try
            {
                // 用系统中文字体，IMGUI 默认字体没有汉字
                cjkFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "Noto Sans CJK SC", "Arial Unicode MS" }, 14);
            }
            catch
            {
                cjkFont = null;
            }

            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = MakeTex(new Color(0.04f, 0.05f, 0.07f, 0.90f));

            titleStyle = MakeLabel(15, FontStyle.Bold, new Color(1f, 0.85f, 0.3f));
            goldStyle = MakeLabel(13, FontStyle.Bold, new Color(1f, 0.78f, 0.25f));
            textStyle = MakeLabel(12, FontStyle.Normal, Color.white);
            hintStyle = MakeLabel(11, FontStyle.Normal, new Color(0.72f, 0.78f, 0.84f));
            rowStyle = MakeLabel(12, FontStyle.Normal, new Color(0.95f, 0.92f, 0.85f));

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false
            };
            if (cjkFont != null)
            {
                buttonStyle.font = cjkFont;
            }

            closeStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0),
                normal = { textColor = new Color(1f, 0.42f, 0.38f) },
                hover = { textColor = new Color(1f, 0.25f, 0.2f) },
                active = { textColor = new Color(1f, 0.2f, 0.15f) }
            };
            if (cjkFont != null)
            {
                closeStyle.font = cjkFont;
            }

            closeLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0),
                wordWrap = false
            };
            closeLabelStyle.normal.textColor = new Color(1f, 0.42f, 0.38f);
            if (cjkFont != null)
            {
                closeLabelStyle.font = cjkFont;
            }
        }

        private static GUIStyle MakeLabel(int size, FontStyle style, Color color)
        {
            var s = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = style,
                wordWrap = false
            };
            s.normal.textColor = color;
            if (cjkFont != null)
            {
                s.font = cjkFont;
            }
            return s;
        }
    }
}


