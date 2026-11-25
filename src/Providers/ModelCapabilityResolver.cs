using System;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Providers
{
    // Deterministic, string-based capability resolver.
    //
    // IMPORTANT HONESTY NOTE: this class does NOT query any provider for a model's true
    // capabilities. There is no universal capability endpoint across the providers this app
    // targets, so we infer from the free-form catalog strings (Provider / Family / Id / Context)
    // using per-family heuristic tables that reflect publicly documented model families as of the
    // knowledge cutoff. These are best-effort classifications, not authoritative facts:
    //   * A model matched to a family inherits that family's typical flags.
    //   * Unknown / unmatched models get CONSERVATIVE defaults (text-only, no tools/vision).
    //   * The Context window is parsed from the catalog string when present and OVERRIDES the
    //     family default, because the catalog is the most model-specific signal available.
    // When a provider SDK later exposes real capability metadata, prefer it over this resolver.
    public static class ModelCapabilityResolver
    {
        public static ModelCapabilities Resolve(ModelItem? model)
        {
            var caps = ModelCapabilities.Default();
            if (model == null) return caps;

            // Normalize all catalog signals into one lowercase haystack for family matching.
            string id = (model.Id ?? string.Empty).ToLowerInvariant();
            string family = (model.Family ?? string.Empty).ToLowerInvariant();
            string provider = (model.Provider ?? string.Empty).ToLowerInvariant();
            string name = (model.Name ?? string.Empty).ToLowerInvariant();
            string hay = string.Join(" ", id, family, provider, name);

            // Apply the first family whose signature matches. Order matters: more specific
            // families (o-series, gpt-4o) are checked before broader ones (generic openai).
            if (!ApplyOpenAiFamily(hay, caps) &&
                !ApplyAnthropicFamily(hay, caps) &&
                !ApplyGeminiFamily(hay, caps) &&
                !ApplyMetaLlamaFamily(hay, caps) &&
                !ApplyMistralFamily(hay, caps) &&
                !ApplyCohereFamily(hay, caps) &&
                !ApplyLocalFamily(hay, provider, caps))
            {
                ApplyGenericFallback(hay, provider, caps);
            }

            // Context string is the most model-specific signal; it overrides family defaults.
            ApplyContextWindow(model.Context, caps);
            return caps;
        }

        // --- OpenAI: GPT-4o (multimodal), o-series (reasoning), GPT-4/4.1, GPT-3.5, embeddings ---
        private static bool ApplyOpenAiFamily(string hay, ModelCapabilities c)
        {
            bool oSeries = Has(hay, "o1", "o3", "o4-mini", "o4 mini", "o-series");
            bool gpt4o = Has(hay, "gpt-4o", "gpt4o", "4o-mini", "chatgpt-4o", "gpt-4.1", "gpt4.1", "gpt-5", "gpt5", "omni");
            bool gpt4 = Has(hay, "gpt-4", "gpt4");
            bool gpt35 = Has(hay, "gpt-3.5", "gpt-35", "gpt3.5", "turbo-instruct");
            bool embed = Has(hay, "text-embedding", "embedding-ada");

            if (embed)
            {
                c.Embeddings = true; c.Streaming = false; c.ToolCalling = false;
                c.Vision = false; c.StructuredOutput = false; c.Reasoning = false;
                c.ContextWindow = 8191; c.InputTokenLimit = 8191; c.OutputTokenLimit = 0;
                return true;
            }
            if (oSeries)
            {
                // Reasoning-first models: tools + structured output, larger output budget, no classic streaming semantics but SSE supported.
                c.Reasoning = true; c.ToolCalling = true; c.StructuredOutput = true; c.Streaming = true;
                c.Vision = Has(hay, "o1", "o3", "o4"); // o-series vision on newer members; conservative but generally true
                c.ContextWindow = 200_000; c.InputTokenLimit = 200_000; c.OutputTokenLimit = 100_000;
                return true;
            }
            if (gpt4o)
            {
                c.Vision = true; c.ToolCalling = true; c.StructuredOutput = true; c.Streaming = true; c.Reasoning = false;
                c.Audio = Has(hay, "audio", "realtime");
                c.ContextWindow = 128_000; c.InputTokenLimit = 128_000; c.OutputTokenLimit = 16_384;
                return true;
            }
            if (gpt4)
            {
                c.Vision = Has(hay, "vision", "turbo"); c.ToolCalling = true; c.StructuredOutput = true; c.Streaming = true;
                c.ContextWindow = 128_000; c.InputTokenLimit = 128_000; c.OutputTokenLimit = 4_096;
                return true;
            }
            if (gpt35)
            {
                c.ToolCalling = true; c.StructuredOutput = false; c.Streaming = true; c.Vision = false;
                c.ContextWindow = 16_385; c.InputTokenLimit = 16_385; c.OutputTokenLimit = 4_096;
                return true;
            }
            return false;
        }

        // --- Anthropic Claude 3 / 3.5 / 3.7 / 4 (multimodal, tools, extended thinking) ---
        private static bool ApplyAnthropicFamily(string hay, ModelCapabilities c)
        {
            if (!Has(hay, "claude", "anthropic")) return false;
            c.Vision = true; c.ToolCalling = true; c.StructuredOutput = true; c.Streaming = true;
            // Extended-thinking / reasoning members: 3.7 Sonnet, Opus 4, "thinking" skus.
            c.Reasoning = Has(hay, "3.7", "3-7", "opus-4", "sonnet-4", "claude-4", "thinking");
            c.ContextWindow = 200_000; c.InputTokenLimit = 200_000;
            c.OutputTokenLimit = c.Reasoning ? 64_000 : 8_192;
            return true;
        }

        // --- Google Gemini 1.5 / 2.x (very large context, multimodal incl. audio/video) ---
        private static bool ApplyGeminiFamily(string hay, ModelCapabilities c)
        {
            if (!Has(hay, "gemini", "palm", "bison")) return false;
            bool gemini = Has(hay, "gemini");
            c.Vision = gemini; c.ToolCalling = gemini; c.StructuredOutput = gemini; c.Streaming = true;
            c.Audio = gemini; // Gemini accepts audio/video parts
            c.Reasoning = Has(hay, "2.5", "2-5", "thinking", "flash-thinking");
            if (Has(hay, "1.5-pro", "1.5 pro", "2.5-pro", "pro-002", "2.0-pro"))
            { c.ContextWindow = 2_000_000; c.InputTokenLimit = 2_000_000; }
            else if (gemini)
            { c.ContextWindow = 1_000_000; c.InputTokenLimit = 1_000_000; }
            else
            { c.ContextWindow = 8_192; c.InputTokenLimit = 8_192; } // legacy PaLM/Bison
            c.OutputTokenLimit = 8_192;
            return true;
        }

        // --- Meta Llama 3.x / 4 (text; vision only on explicit vision/multimodal skus) ---
        private static bool ApplyMetaLlamaFamily(string hay, ModelCapabilities c)
        {
            if (!Has(hay, "llama", "codellama", "meta-llama")) return false;
            c.ToolCalling = Has(hay, "llama-3", "llama3", "llama-4", "llama4"); // tool use from Llama 3.1+
            c.Vision = Has(hay, "vision", "llama-3.2", "llama-4", "scout", "maverick");
            c.StructuredOutput = c.ToolCalling; c.Streaming = true; c.Reasoning = false;
            c.ContextWindow = Has(hay, "llama-3.1", "llama-3.2", "llama-3.3", "llama-4") ? 128_000 : 8_192;
            c.InputTokenLimit = c.ContextWindow; c.OutputTokenLimit = 4_096;
            return true;
        }

        // --- Mistral / Mixtral (tools + structured output; Pixtral is the vision sku) ---
        private static bool ApplyMistralFamily(string hay, ModelCapabilities c)
        {
            if (!Has(hay, "mistral", "mixtral", "codestral", "pixtral", "ministral", "magistral")) return false;
            c.ToolCalling = true; c.StructuredOutput = true; c.Streaming = true;
            c.Vision = Has(hay, "pixtral");
            c.Reasoning = Has(hay, "magistral"); // Magistral is Mistral's reasoning line
            c.ContextWindow = Has(hay, "large", "codestral", "pixtral", "ministral") ? 128_000 : 32_000;
            c.InputTokenLimit = c.ContextWindow; c.OutputTokenLimit = 4_096;
            return true;
        }

        // --- Cohere Command R / R+ (tools + RAG; embeddings for embed-* skus) ---
        private static bool ApplyCohereFamily(string hay, ModelCapabilities c)
        {
            if (!Has(hay, "command", "cohere", "aya")) return false;
            if (Has(hay, "embed"))
            {
                c.Embeddings = true; c.Streaming = false; c.ToolCalling = false; c.StructuredOutput = false;
                c.ContextWindow = 512; c.InputTokenLimit = 512; c.OutputTokenLimit = 0;
                return true;
            }
            c.ToolCalling = Has(hay, "command-r", "command r"); c.StructuredOutput = c.ToolCalling; c.Streaming = true;
            c.Vision = false;
            c.ContextWindow = Has(hay, "command-r", "command r") ? 128_000 : 4_096;
            c.InputTokenLimit = c.ContextWindow; c.OutputTokenLimit = 4_096;
            return true;
        }

        // --- Local runtimes (Ollama, LM Studio, llama.cpp, GPT4All, vLLM) ---
        // We can't know the underlying weights reliably, so keep flags conservative but assume
        // streaming (these runtimes stream by default) and modest context unless the catalog says more.
        private static bool ApplyLocalFamily(string hay, string provider, ModelCapabilities c)
        {
            bool local = Has(provider, "ollama", "local", "lmstudio", "lm studio", "llamacpp", "llama.cpp", "gpt4all", "vllm", "koboldcpp", "localai")
                      || Has(hay, "ollama", "gguf", "ggml");
            if (!local) return false;
            c.Streaming = true; c.ToolCalling = false; c.StructuredOutput = false; c.Vision = false; c.Reasoning = false;
            // Best-effort: if the local model name embeds a known family, borrow its vision/tools flags.
            if (Has(hay, "llava", "bakllava", "moondream", "vision", "pixtral", "qwen2-vl", "qwen2.5-vl", "minicpm-v")) c.Vision = true;
            if (Has(hay, "qwen", "llama-3", "mistral", "hermes", "functionary", "firefunction")) c.ToolCalling = true;
            if (Has(hay, "deepseek-r1", "qwq", "reasoning", "-r1")) c.Reasoning = true;
            c.ContextWindow = 8_192; c.InputTokenLimit = 8_192; c.OutputTokenLimit = 4_096;
            return true;
        }

        // --- Generic OpenAI-compatible / unknown endpoints ---
        // Most aggregator + custom endpoints speak the OpenAI chat schema, which supports streaming
        // and (usually) tool calling. We enable those two but leave vision/reasoning off unless the
        // name hints otherwise, and keep a modest context window. Conservative on purpose.
        private static void ApplyGenericFallback(string hay, string provider, ModelCapabilities c)
        {
            bool openAiCompatible = Has(provider, "openai", "openrouter", "together", "groq", "fireworks", "deepinfra", "perplexity", "azure", "anyscale", "nvidia", "xai", "grok")
                                 || Has(hay, "gpt", "grok", "deepseek", "qwen", "yi", "phi", "gemma");
            c.Streaming = true;
            c.ToolCalling = openAiCompatible;
            c.StructuredOutput = false;
            c.Vision = Has(hay, "vision", "-vl", "multimodal", "grok-2-vision", "grok-4");
            c.Reasoning = Has(hay, "reasoner", "deepseek-r1", "-r1", "qwq", "thinking", "grok-3", "grok-4");
            c.ContextWindow = 8_192; c.InputTokenLimit = 8_192; c.OutputTokenLimit = 4_096;
        }

        // Parses the free-form catalog "Context" string (e.g. "128K", "200000", "1M", "32k tokens")
        // and, when valid, overrides the family default. Silent no-op on unparseable input.
        internal static void ApplyContextWindow(string? context, ModelCapabilities c)
        {
            if (string.IsNullOrWhiteSpace(context)) return;
            var raw = context.Trim().ToLowerInvariant()
                .Replace("tokens", "").Replace("token", "").Replace(",", "").Replace("_", "").Trim();
            double mult = 1;
            if (raw.EndsWith("k")) { mult = 1_000; raw = raw[..^1].Trim(); }
            else if (raw.EndsWith("m")) { mult = 1_000_000; raw = raw[..^1].Trim(); }
            if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0)
            {
                int window = (int)Math.Min(int.MaxValue, n * mult);
                c.ContextWindow = window;
                c.InputTokenLimit = window;
            }
        }

        private static bool Has(string haystack, params string[] needles)
        {
            foreach (var n in needles)
                if (!string.IsNullOrEmpty(n) && haystack.Contains(n, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
