using System;

namespace LlmodelBox.Common
{
    public static class MaskHelper
    {
        public static string MaskKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "[NOT CONFIGURED]";
            if (key.Length <= 8) return "********";
            return key.Substring(0, 4) + "..." + key.Substring(key.Length - 4);
        }
    }
}
