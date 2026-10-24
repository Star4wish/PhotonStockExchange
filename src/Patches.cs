using System;
using HarmonyLib;
using UnityEngine;

namespace StockExchange
{
    /// <summary>
    /// Harmony 补丁集合：
    /// 1) FactorySystem.GameTick 之后结算交易所交易（保留；主驱动在 MarketOverlay.Update 里）；
    /// 2) 解锁通关科技时补上交易所配方；
    /// 3) 拆除交易所时（正常拆除 / 瞬间拆除两条路径）返还缓冲中的物品；
    /// 4) 把原版分馏塔窗口里的内容替换成交易所自己的提示。
    /// </summary>
    internal static class Patches
    {
        [HarmonyPatch(typeof(FactorySystem), nameof(FactorySystem.GameTick))]
        internal static class FactorySystemGameTickPostfix
        {
            [HarmonyPostfix]
            internal static void Postfix(FactorySystem __instance)
            {
                try
                {
                    ExchangeLogic.TickFactory(__instance);
                }
                catch (Exception e)
                {
                    Plugin.LogError("GameTick postfix failed", e);
                }
            }
        }

        [HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.UnlockTech))]
        internal static class UnlockTechPostfix
        {
            [HarmonyPostfix]
            internal static void Postfix(GameHistoryData __instance, int techId)
            {
                try
                {
                    if (techId == Plugin.UnlockTechId.Value)
                    {
                        ExchangeLogic.EnsureRecipeUnlocked();
                    }
                }
                catch (Exception e)
                {
                    Plugin.LogError("UnlockTech postfix failed", e);
                }
            }
        }

        // ===== 拆除返还：正常拆除路径 =====
        [HarmonyPatch(typeof(FactorySystem), nameof(FactorySystem.TakeBackItems_Fractionator))]
        internal static class TakeBackItemsFractionatorPrefix
        {
            [HarmonyPrefix]
            internal static void Prefix(FactorySystem __instance, Player player, int fracId)
            {
                try
                {
                    if (!TryGetExchange(__instance, fracId, out _))
                    {
                        return;
                    }
                    ref FractionatorComponent frac = ref __instance.fractionatorPool[fracId];
                    Plugin.LogInfo($"[refund] TakeBackItems_Fractionator path, fracId={fracId}");
                    ExchangeLogic.RefundPending(ref frac, player);
                }
                catch (Exception e)
                {
                    Plugin.LogError("TakeBackItems prefix failed", e);
                }
            }
        }

        // ===== 拆除返还：瞬间拆除 / 沙盒拆除路径（ClearItemsInEntity） =====
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.ClearItemsInEntity))]
        internal static class ClearItemsInEntityPrefix
        {
            [HarmonyPrefix]
            internal static void Prefix(PlanetFactory __instance, int entityId)
            {
                try
                {
                    if (__instance == null || __instance.entityPool == null ||
                        entityId <= 0 || entityId >= __instance.entityPool.Length)
                    {
                        return;
                    }
                    if (__instance.entityPool[entityId].protoId != Plugin.ItemExchange)
                    {
                        return;
                    }
                    var fs = __instance.factorySystem;
                    if (fs == null || fs.fractionatorPool == null)
                    {
                        return;
                    }
                    int fracId = __instance.entityPool[entityId].fractionatorId;
                    if (!TryGetExchange(fs, fracId, out _))
                    {
                        return;
                    }
                    ref FractionatorComponent frac = ref fs.fractionatorPool[fracId];
                    Plugin.LogInfo($"[refund] ClearItemsInEntity path, entityId={entityId} fracId={fracId}");
                    ExchangeLogic.RefundPending(ref frac, GameMain.mainPlayer);
                }
                catch (Exception e)
                {
                    Plugin.LogError("ClearItemsInEntity prefix failed", e);
                }
            }
        }

        // ===== 原版分馏塔窗口文案替换 =====
        [HarmonyPatch(typeof(UIFractionatorWindow), "_OnUpdate")]
        internal static class FractionatorWindowUpdatePostfix
        {
            [HarmonyPostfix]
            internal static void Postfix(UIFractionatorWindow __instance)
            {
                try
                {
                    var window = __instance;
                    if (window == null || window.fractionatorId <= 0 || window.factorySystem == null)
                    {
                        return;
                    }
                    if (!ExchangeLogic.TryGetExchange(window.factorySystem, window.fractionatorId, out FractionatorComponent frac))
                    {
                        return;
                    }

                    var fs = window.factorySystem;
                    var star = fs.planet != null ? fs.planet.star : null;
                    if (star == null)
                    {
                        return;
                    }

                    int mode = ExchangeLogic.GetMode(in frac);
                    double price = PriceService.GetPrice(star);
                    PriceService.GetQuotes(price, out int sailsPerCoin, out double sailsPerCoinSold);
                    float power = ExchangeLogic.GetPowerRatio(fs, in frac);

                    string status;
                    if (power <= 0.1f)
                    {
                        status = L10N.T("未供电", "No power");
                    }
                    else if (power < 0.99f)
                    {
                        status = L10N.T("电力不足", "Low power");
                    }
                    else if (frac.fluidInputCount > 0 || frac.productOutputCount > 0)
                    {
                        status = L10N.T("运转中", "Running");
                    }
                    else
                    {
                        status = L10N.T("待机（等待原料）", "Idle (waiting for input)");
                    }

                    if (window.titleText != null)
                    {
                        window.titleText.text = L10N.T("光子证券交易所", "Photon Stock Exchange");
                    }

                    // 统一使用原版“两侧传送带进货”那种干净字体（remindText 的字体/材质/颜色），
                    // 用 historyText 那块大文本区按行左对齐显示，避免原版 30 号大字重叠与发光糊字。
                    // 同时隐藏产物/原料图标按钮（我们不是原版分馏，那些占位框会留下绿色高亮块）。
                    HideProductWidgets(window);
                    var styleSrc = window.remindText;
                    if (window.stateText != null)
                    {
                        // 只保留“未供电/电力不足”这类必要告警，正常时让窗口保持干净（只显示模式与税后比例）。
                        window.stateText.text = (power < 0.99f) ? status : string.Empty;
                        ApplyPlainStyle(window.stateText, styleSrc);
                        window.stateText.fontSize = 14;
                    }
                    if (window.remindText != null)
                    {
                        // 原版提示行与正文区域会重叠：隐藏原版提示，把提示并正文最后一行统一排版。
                        window.remindText.enabled = false;
                        window.remindText.text = string.Empty;
                    }
                    if (window.historyText != null)
                    {
                        window.historyText.enabled = true;
                        ApplyPlainStyle(window.historyText, styleSrc);
                        window.historyText.fontSize = 13;
                        window.historyText.alignment = TextAnchor.UpperLeft;
                        window.historyText.lineSpacing = 1.25f;
                        window.historyText.horizontalOverflow = HorizontalWrapMode.Wrap;
                        window.historyText.verticalOverflow = VerticalWrapMode.Overflow;
                        window.historyText.text =
                            string.Format(L10N.T("当前模式：{0}", "Mode: {0}"), ExchangeLogic.ModeName(mode)) + "\r\n" +
                            string.Format(L10N.T("税后：买币 {0} 帆 = 1 币", "After tax: buy {0} sails = 1 coin"), sailsPerCoin) + "\r\n" +
                            string.Format(L10N.T("税后：卖币 1 币 = {0:0.000} 帆", "After tax: sell 1 coin = {0:0.000} sails"), sailsPerCoinSold) + "\r\n" +
                            L10N.T("两侧传送带进货，中间传送带出货（无延迟）。", "Side belts = input, middle belt = output (no delay).");
                        AlignTextLeftToTitle(window);
                    }
                }
                catch (Exception e)
                {
                    Plugin.LogError("FractionatorWindow postfix failed", e);
                }
            }
        }

        /// <summary>把正文文本区收窄到窗口内，并让它的左边缘与标题“光子证券交易所”的左边缘对齐（窗口根节点坐标空间计算，兼容不同父节点）。</summary>
        internal static void AlignTextLeftToTitle(UIFractionatorWindow window)
        {
            try
            {
                var root = window.transform as RectTransform;
                var title = window.titleText != null ? window.titleText.rectTransform : null;
                var body = window.historyText != null ? window.historyText.rectTransform : null;
                if (root == null || title == null || body == null)
                {
                    return;
                }
                float winW = root.rect.width;
                if (winW < 80f)
                {
                    winW = 420f;
                }
                float targetW = Mathf.Max(140f, winW - 56f);
                body.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetW);

                Vector3 titleLocal = root.InverseTransformPoint(title.position);
                Vector3 bodyLocal = root.InverseTransformPoint(body.position);
                float titleLeft = titleLocal.x - title.rect.width * title.pivot.x;
                float bodyLeft = bodyLocal.x - targetW * body.pivot.x;
                float shift = titleLeft - bodyLeft;

                // 竖向：在“供电一行下方”到“窗口底端”之间整体居中，避免文字太靠下。
                float lineH = Mathf.Max(12f, window.historyText.fontSize * Mathf.Max(1f, window.historyText.lineSpacing) + 3f);
                float blockH = lineH * 4f;
                var power = window.powerText != null ? window.powerText.rectTransform : null;
                float bandTop = power != null
                    ? root.InverseTransformPoint(power.position).y - power.rect.height * 0.5f - 6f
                    : root.rect.yMax - 90f;
                float bandBottom = root.rect.yMin + 14f;
                if (bandBottom > bandTop - blockH)
                {
                    bandBottom = bandTop - blockH;
                }
                float bandCenter = (bandTop + bandBottom) * 0.5f;
                float yTarget = bandCenter + (body.pivot.y - 0.5f) * blockH;
                body.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, blockH);
                body.position = root.TransformPoint(new Vector3(bodyLocal.x + shift, yTarget, bodyLocal.z));
            }
            catch (Exception e)
            {
                Plugin.LogError("align text failed", e);
            }
        }

        /// <summary>隐藏原版分馏塔的原料/产物图标与按钮：本建筑不是原版分馏，保留会留下绿色高亮占位块。</summary>
        internal static void HideProductWidgets(UIFractionatorWindow window)
        {
            try
            {
                if (window.productBox != null) window.productBox.SetActive(false);
                if (window.oriProductBox != null) window.oriProductBox.SetActive(false);
                if (window.productButton != null) window.productButton.gameObject.SetActive(false);
                if (window.oriProductButton != null) window.oriProductButton.gameObject.SetActive(false);
                if (window.needIcon != null) window.needIcon.enabled = false;
                if (window.productIcon != null) window.productIcon.enabled = false;
                if (window.oriProductIcon != null) window.oriProductIcon.enabled = false;
                if (window.needIncs != null)
                {
                    for (int i = 0; i < window.needIncs.Length; i++) if (window.needIncs[i] != null) window.needIncs[i].enabled = false;
                }
                if (window.oriProductIncs != null)
                {
                    for (int i = 0; i < window.oriProductIncs.Length; i++) if (window.oriProductIncs[i] != null) window.oriProductIncs[i].enabled = false;
                }
                if (window.speedArrows != null)
                {
                    for (int i = 0; i < window.speedArrows.Length; i++) if (window.speedArrows[i] != null) window.speedArrows[i].enabled = false;
                }
            }
            catch (Exception e)
            {
                Plugin.LogError("hide product widgets failed", e);
            }
        }

        /// <summary>把目标文本组件改成与“两侧传送带进货”提示一致的干净样式（无原版发光/描边、不解析富文本）。</summary>
        internal static void ApplyPlainStyle(UnityEngine.UI.Text target, UnityEngine.UI.Text styleSource)
        {
            if (target == null || styleSource == null)
            {
                return;
            }
            target.font = styleSource.font;
            target.material = styleSource.material;
            target.color = styleSource.color;
            target.fontSize = styleSource.fontSize;
            target.lineSpacing = 1.1f;
            target.supportRichText = false;
        }

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
    }
}
