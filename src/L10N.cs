using System;

namespace StockExchange
{
    /// <summary>
    /// 极简本地化：游戏语言为中文（简/繁）时显示中文，其它语言显示英文。
    /// IMGUI 面板的文案在运行时按此切换；物品/建筑名称与描述由 CommonAPI 的 LocalizationModule 负责。
    /// </summary>
    internal static class L10N
    {
        private static int cachedLcid = int.MinValue;
        private static bool cachedIsChinese;

        public static bool IsChinese
        {
            get
            {
                try
                {
                    int lcid = Localization.CurrentLanguageLCID;
                    if (lcid != cachedLcid)
                    {
                        cachedLcid = lcid;
                        cachedIsChinese = lcid == 2052 || lcid == 1028; // 2052 简体中文，1028 繁体中文
                    }
                    return cachedIsChinese;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string T(string cn, string en)
        {
            return IsChinese ? cn : en;
        }
    }
}
