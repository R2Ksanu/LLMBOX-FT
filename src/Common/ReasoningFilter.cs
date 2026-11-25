using System;
using System.Text.RegularExpressions;

namespace LlmodelBox.Common
{
    // Separates a model's exposed reasoning/thinking from the answer the user should see.
    // CloudApiService prepends provider reasoning as:
    //   [<MODEL> REASONING / THINKING]\r\n<reasoning>\r\n----(dashes)----\r\n<answer>
    // Some models instead emit <think>...</think> / <thinking>...</thinking> inline.
    // The chat bubble shows only the answer; the reasoning is routed to the Neural Net view.
    public static class ReasoningFilter
    {
        private static readonly Regex HeaderRx = new(
            @"^\s*\[[^\]]*REASONING\s*/\s*THINKING[^\]]*\]\r?\n(?<body>.*?)\r?\n-{10,}\r?\n(?<answer>.*)$",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly Regex ThinkTagRx = new(
            @"<think(?:ing)?>(?<body>.*?)</think(?:ing)?>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Returns the clean answer; hands back any extracted reasoning via out param.
        public static string Split(string response, out string reasoning)
        {
            reasoning = string.Empty;
            if (string.IsNullOrEmpty(response)) return response ?? string.Empty;

            var header = HeaderRx.Match(response);
            if (header.Success)
            {
                reasoning = header.Groups["body"].Value.Trim();
                return header.Groups["answer"].Value.Trim();
            }

            var tag = ThinkTagRx.Match(response);
            if (tag.Success)
            {
                reasoning = tag.Groups["body"].Value.Trim();
                return ThinkTagRx.Replace(response, string.Empty).Trim();
            }

            return response;
        }
    }
}
