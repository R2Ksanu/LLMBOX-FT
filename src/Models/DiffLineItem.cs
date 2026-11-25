using System;

namespace LlmodelBox.Models
{
    public class DiffLineItem
    {
        public int? LineNumberOld { get; set; }
        public int? LineNumberNew { get; set; }
        public string Type { get; set; } = "Unchanged"; // Added, Removed, Unchanged, Header
        public string Content { get; set; } = string.Empty;
        public string Prefix { get; set; } = " ";

        public string DisplayOldLine => LineNumberOld.HasValue ? LineNumberOld.Value.ToString() : "";
        public string DisplayNewLine => LineNumberNew.HasValue ? LineNumberNew.Value.ToString() : "";
    }
}
