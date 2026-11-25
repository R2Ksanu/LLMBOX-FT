using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LlmodelBox.Common;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    public class ModelLibraryService : IModelLibraryService
    {
        private readonly string _modelsFilePath;

        public ModelLibraryService(string? modelsFilePath = null)
        {
            _modelsFilePath = modelsFilePath ?? AppConstants.ModelsFilePath;
        }

        public List<ModelItem> LoadModels()
        {
            var list = new List<ModelItem>();
            try
            {
                if (File.Exists(_modelsFilePath))
                {
                    string json = File.ReadAllText(_modelsFilePath);
                    var loaded = JsonSerializer.Deserialize<List<ModelItem>>(json);
                    if (loaded != null && loaded.Count > 0)
                    {
                        list = loaded;
                    }
                }
            }
            catch { }

            if (list.Count == 0)
            {
                list = GetDefaultModels();
            }

            return list;
        }

        public bool SaveModels(List<ModelItem> models)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(models, options);
                LlmodelBox.Common.AtomicFile.WriteAllText(_modelsFilePath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void RegisterCustomProviderModel(CustomProviderItem provider, List<ModelItem> allModels, List<ModelItem> filteredModels)
        {
            var modelItem = new ModelItem
            {
                Id = provider.Model,
                Name = $"{provider.Name}: {provider.Model}",
                Provider = provider.Name,
                Endpoint = provider.BaseUrl,
                Family = "Custom Cloud Provider",
                Format = "OpenAI REST",
                Context = "Custom API",
                Parameters = "Cloud Managed",
                Pricing = "Custom Key",
                Status = "API Ready",
                Description = $"Custom registered provider at {provider.BaseUrl}. Direct Bearer auth.",
                Icon = "⚡",
                WebUrl = provider.WebUrl,
                LocalLogoPath = provider.LocalLogoPath,
                IsCustom = true,
                CustomApiKey = provider.ApiKey
            };

            allModels.RemoveAll(m => m.Id == modelItem.Id && m.IsCustom);
            filteredModels.RemoveAll(m => m.Id == modelItem.Id && m.IsCustom);

            allModels.Add(modelItem);
            filteredModels.Add(modelItem);
        }

        public List<ModelItem> FilterModels(List<ModelItem> allModels, string filterPill, string searchQuery)
        {
            IEnumerable<ModelItem> query = allModels;

            if (string.Equals(filterPill, "OpenAI", StringComparison.OrdinalIgnoreCase))
                query = query.Where(m => m.Provider.Contains("OpenAI", StringComparison.OrdinalIgnoreCase));
            else if (string.Equals(filterPill, "Anthropic", StringComparison.OrdinalIgnoreCase))
                query = query.Where(m => m.Provider.Contains("Anthropic", StringComparison.OrdinalIgnoreCase));
            else if (string.Equals(filterPill, "Google", StringComparison.OrdinalIgnoreCase) || string.Equals(filterPill, "Google Gemini", StringComparison.OrdinalIgnoreCase))
                query = query.Where(m => m.Provider.Contains("Google", StringComparison.OrdinalIgnoreCase) || m.Provider.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
            else if (string.Equals(filterPill, "xAI", StringComparison.OrdinalIgnoreCase) || string.Equals(filterPill, "Grok", StringComparison.OrdinalIgnoreCase))
                query = query.Where(m => m.Provider.Contains("xAI", StringComparison.OrdinalIgnoreCase) || m.Provider.Contains("Grok", StringComparison.OrdinalIgnoreCase));
            else if (string.Equals(filterPill, "DeepSeek", StringComparison.OrdinalIgnoreCase))
                query = query.Where(m => m.Provider.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase));
            else if (string.Equals(filterPill, "Groq", StringComparison.OrdinalIgnoreCase))
                query = query.Where(m => m.Provider.Contains("Groq", StringComparison.OrdinalIgnoreCase));
            else if (string.Equals(filterPill, "OpenRouter", StringComparison.OrdinalIgnoreCase))
                query = query.Where(m => m.Provider.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase));
            else if (string.Equals(filterPill, "Mistral", StringComparison.OrdinalIgnoreCase))
                query = query.Where(m => m.Provider.Contains("Mistral", StringComparison.OrdinalIgnoreCase));
            else if (string.Equals(filterPill, "Custom", StringComparison.OrdinalIgnoreCase))
                query = query.Where(m => m.IsCustom);

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                string s = searchQuery.Trim().ToLowerInvariant();
                query = query.Where(m =>
                    m.Name.ToLowerInvariant().Contains(s) ||
                    m.Id.ToLowerInvariant().Contains(s) ||
                    m.Provider.ToLowerInvariant().Contains(s) ||
                    m.Family.ToLowerInvariant().Contains(s) ||
                    m.Description.ToLowerInvariant().Contains(s)
                );
            }

            return query.ToList();
        }

        public List<CompanyCategoryItem> GetCompanyCategories(List<ModelItem> allModels, UserSettings settings, ICloudApiService cloudApiService)
        {
            var companyDefs = new[]
            {
                new { Id = "openai", Name = "OpenAI", Tagline = "Frontier multimodal foundation models & reasoning systems", WebUrl = "https://openai.com", KeyDocUrl = "https://platform.openai.com/api-keys", EnvVar = "OPENAI_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "openai.com.png"), Icon = "🟢", Pattern = "OpenAI" },
                new { Id = "anthropic", Name = "Anthropic", Tagline = "Industry-standard safety, coding intelligence & agentic reasoning in Claude 3.5", WebUrl = "https://anthropic.com", KeyDocUrl = "https://console.anthropic.com/settings/keys", EnvVar = "ANTHROPIC_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "anthropic.com.png"), Icon = "🟠", Pattern = "Anthropic" },
                new { Id = "google", Name = "Google DeepMind / Gemini", Tagline = "Massive 2-million-token context windows & native audio/video multimodal understanding", WebUrl = "https://aistudio.google.com", KeyDocUrl = "https://aistudio.google.com/apikey", EnvVar = "GEMINI_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "google.com.png"), Icon = "🔵", Pattern = "Google" },
                new { Id = "xai", Name = "xAI (Grok)", Tagline = "Truth-seeking frontier conversational models with high-speed coding and logic", WebUrl = "https://x.ai", KeyDocUrl = "https://console.x.ai", EnvVar = "XAI_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "x.ai.png"), Icon = "⚪", Pattern = "xAI" },
                new { Id = "deepseek", Name = "DeepSeek", Tagline = "Open-architecture 671B Mixture-of-Experts and pure reasoning reinforcement models", WebUrl = "https://deepseek.com", KeyDocUrl = "https://platform.deepseek.com/api_keys", EnvVar = "DEEPSEEK_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "deepseek.com.png"), Icon = "🟣", Pattern = "DeepSeek" },
                new { Id = "groq", Name = "Groq", Tagline = "LPU Inference Engine delivering ultra-fast 300+ tokens/sec deterministic throughput", WebUrl = "https://console.groq.com", KeyDocUrl = "https://console.groq.com/keys", EnvVar = "GROQ_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "groq.com.png"), Icon = "⚡", Pattern = "Groq" },
                new { Id = "openrouter", Name = "OpenRouter", Tagline = "Unified API gateway providing access to 200+ models with automatic failover", WebUrl = "https://openrouter.ai", KeyDocUrl = "https://openrouter.ai/keys", EnvVar = "OPENROUTER_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "openrouter.ai.png"), Icon = "🌐", Pattern = "OpenRouter" },
                new { Id = "cloudflare", Name = "Cloudflare Workers AI", Tagline = "Serverless GPU inference running across Cloudflare 300+ global edge cities", WebUrl = "https://developers.cloudflare.com/workers-ai", KeyDocUrl = "https://dash.cloudflare.com/profile/api-tokens", EnvVar = "CLOUDFLARE_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "cloudflare.com.png"), Icon = "☁️", Pattern = "Cloudflare" },
                new { Id = "mistral", Name = "Mistral AI", Tagline = "European frontier AI models with world-class coding, math, and multilingual mastery", WebUrl = "https://console.mistral.ai", KeyDocUrl = "https://console.mistral.ai/api-keys", EnvVar = "MISTRAL_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "mistral.ai.png"), Icon = "🌪️", Pattern = "Mistral" },
                new { Id = "huggingface", Name = "Hugging Face", Tagline = "Serverless Inference API accessing the world's largest open-weights model hub", WebUrl = "https://huggingface.co", KeyDocUrl = "https://huggingface.co/settings/tokens", EnvVar = "HUGGINGFACE_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "huggingface.co.png"), Icon = "🤗", Pattern = "Hugging" },
                new { Id = "cohere", Name = "Cohere", Tagline = "Enterprise conversational AI optimized for multi-step reasoning, citations, and RAG", WebUrl = "https://dashboard.cohere.com", KeyDocUrl = "https://dashboard.cohere.com/api-keys", EnvVar = "COHERE_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "cohere.com.png"), Icon = "💠", Pattern = "Cohere" },
                new { Id = "nvidia", Name = "NVIDIA NIM", Tagline = "Microservices accelerated by TensorRT-LLM and Triton on enterprise NVIDIA GPUs", WebUrl = "https://build.nvidia.com", KeyDocUrl = "https://build.nvidia.com", EnvVar = "NVIDIA_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "nvidia.com.png"), Icon = "🟢", Pattern = "NVIDIA" },
                new { Id = "sambanova", Name = "SambaNova Cloud", Tagline = "Hardware-native inference on SN40L Reconfigurable Dataflow Units (RDUs)", WebUrl = "https://cloud.sambanova.ai", KeyDocUrl = "https://cloud.sambanova.ai/apis", EnvVar = "SAMBANOVA_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "sambanova.ai.png"), Icon = "🔷", Pattern = "SambaNova" },
                new { Id = "zhipu", Name = "Z.AI / Zhipu", Tagline = "Leading bilingual foundation model GLM-4 with deep STEM and math intelligence", WebUrl = "https://open.bigmodel.cn", KeyDocUrl = "https://open.bigmodel.cn/usercenter/apikeys", EnvVar = "ZHIPU_API_KEY", Logo = Path.Combine(AppConstants.CacheDir, "bigmodel.cn.png"), Icon = "🇨🇳", Pattern = "Zhipu" }
            };

            var categories = new List<CompanyCategoryItem>();

            foreach (var def in companyDefs)
            {
                string key = cloudApiService.GetApiKeyForProvider(def.Name, settings);
                bool hasKey = !string.IsNullOrWhiteSpace(key);
                string masked = MaskHelper.MaskKey(key);

                var modelsForCompany = allModels
                    .Where(m => !m.IsCustom && (m.Provider.Contains(def.Pattern, StringComparison.OrdinalIgnoreCase) || def.Name.Contains(m.Provider, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                // Update HasApiKey on each model item
                foreach (var m in modelsForCompany)
                {
                    m.HasApiKey = hasKey;
                }

                categories.Add(new CompanyCategoryItem
                {
                    Id = def.Id,
                    Name = def.Name,
                    Tagline = def.Tagline,
                    WebUrl = def.WebUrl,
                    BaseUrl = modelsForCompany.FirstOrDefault()?.Endpoint ?? "",
                    // Real brand logo (Clearbit CDN) cached on disk at def.Logo; falls back to the
                    // curated glyph (def.Icon) until the logo is fetched. The visibility bindings on
                    // CompanyCategoryItem ensure exactly one of them renders — never both.
                    LocalLogoPath = def.Logo,
                    FallbackIcon = def.Icon,
                    HasApiKey = hasKey,
                    MaskedApiKey = masked,
                    KeyDocUrl = def.KeyDocUrl,
                    EnvVarName = def.EnvVar,
                    Models = modelsForCompany
                });
            }

            // Custom Providers Category
            var customModels = allModels.Where(m => m.IsCustom).ToList();
            if (customModels.Count > 0)
            {
                foreach (var m in customModels)
                {
                    m.HasApiKey = !string.IsNullOrWhiteSpace(m.CustomApiKey);
                }

                categories.Add(new CompanyCategoryItem
                {
                    Id = "custom-providers",
                    Name = "Custom Registered Providers",
                    Tagline = "User-defined cloud endpoints and third-party AI gateway connections",
                    WebUrl = "https://llmodelbox.local",
                    BaseUrl = "Multiple Endpoints",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "together.ai.png"),
                    FallbackIcon = "⚡",
                    HasApiKey = customModels.All(m => m.HasApiKey),
                    MaskedApiKey = customModels.Count > 0 ? $"{customModels.Count} Endpoints" : "[NO ENDPOINTS]",
                    KeyDocUrl = "",
                    EnvVarName = "CUSTOM_KEYS",
                    Models = customModels
                });
            }

            return categories;
        }

        public List<CompanyCategoryItem> FilterCompanyCategories(List<CompanyCategoryItem> categories, string filterPill, string searchQuery)
        {
            IEnumerable<CompanyCategoryItem> query = categories;

            bool freeFilter = string.Equals(filterPill, "Free", StringComparison.OrdinalIgnoreCase);
            bool paidFilter = string.Equals(filterPill, "Paid", StringComparison.OrdinalIgnoreCase);
            bool tierFilter = freeFilter || paidFilter;

            if (!tierFilter && !string.Equals(filterPill, "All", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(filterPill))
            {
                if (string.Equals(filterPill, "Custom", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(c => c.Id == "custom-providers");
                }
                else
                {
                    query = query.Where(c => c.Name.Contains(filterPill, StringComparison.OrdinalIgnoreCase) || c.Id.Contains(filterPill, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                string s = searchQuery.Trim().ToLowerInvariant();
                var searchProjected = new List<CompanyCategoryItem>();
                foreach (var c in query)
                {
                    bool catMatches = c.Name.ToLowerInvariant().Contains(s) || c.Tagline.ToLowerInvariant().Contains(s);
                    var matchingModels = (c.Models ?? new List<ModelItem>())
                        .Where(m => catMatches || m.Name.ToLowerInvariant().Contains(s) || m.Id.ToLowerInvariant().Contains(s) || m.Description.ToLowerInvariant().Contains(s))
                        .ToList();
                    if (matchingModels.Count > 0)
                    {
                        searchProjected.Add(CloneCategoryWithModels(c, matchingModels));
                    }
                }
                query = searchProjected;
            }

            // Free / Paid pills filter at the MODEL level: project each category down to just its free
            // (or paid) models and drop categories left with none. Copies the category so the shared
            // aggregator cache and the unfiltered list are never mutated.
            if (tierFilter)
            {
                var projected = new List<CompanyCategoryItem>();
                foreach (var c in query)
                {
                    var models = (c.Models ?? new List<ModelItem>()).Where(m => m.IsFree == freeFilter).ToList();
                    if (models.Count == 0) continue;
                    projected.Add(CloneCategoryWithModels(c, models));
                }
                return projected;
            }

            return query.ToList();
        }

        private static CompanyCategoryItem CloneCategoryWithModels(CompanyCategoryItem source, List<ModelItem> models)
        {
            return new CompanyCategoryItem
            {
                Id = source.Id,
                Name = source.Name,
                Tagline = source.Tagline,
                WebUrl = source.WebUrl,
                BaseUrl = source.BaseUrl,
                LocalLogoPath = source.LocalLogoPath,
                FallbackIcon = source.FallbackIcon,
                HasApiKey = source.HasApiKey,
                MaskedApiKey = source.MaskedApiKey,
                KeyDocUrl = source.KeyDocUrl,
                EnvVarName = source.EnvVarName,
                Models = models
            };
        }

        public List<ModelItem> GetDefaultModels()
        {
            return new List<ModelItem>
            {
                // OpenAI
                new ModelItem
                {
                    Id = "gpt-4o",
                    Name = "GPT-4o",
                    Provider = "OpenAI",
                    Endpoint = "https://api.openai.com/v1/chat/completions",
                    Family = "Multimodal Flagship",
                    Format = "Cloud REST API",
                    Context = "128k",
                    Parameters = "Omni Frontier",
                    Pricing = "$2.50 / $10.00 MTok",
                    Status = "API Ready",
                    Description = "OpenAI flagship high-intelligence multimodal model for complex reasoning, code generation, and vision.",
                    Icon = "🟢",
                    WebUrl = "https://openai.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "openai.com.png")
                },
                new ModelItem
                {
                    Id = "gpt-4o-mini",
                    Name = "GPT-4o Mini",
                    Provider = "OpenAI",
                    Endpoint = "https://api.openai.com/v1/chat/completions",
                    Family = "High Speed / Cost-Effective",
                    Format = "Cloud REST API",
                    Context = "128k",
                    Parameters = "Compact Omni",
                    Pricing = "$0.15 / $0.60 MTok",
                    Status = "API Ready",
                    Description = "OpenAI streamlined multimodal model optimized for sub-second latency and economical workflows.",
                    Icon = "🟢",
                    WebUrl = "https://openai.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "openai.com.png")
                },
                new ModelItem
                {
                    Id = "o1-mini",
                    Name = "OpenAI o1-Mini",
                    Provider = "OpenAI",
                    Endpoint = "https://api.openai.com/v1/chat/completions",
                    Family = "Advanced STEM Reasoning",
                    Format = "Cloud REST API",
                    Context = "128k",
                    Parameters = "Reasoning Frontier",
                    Pricing = "$3.00 / $12.00 MTok",
                    Status = "API Ready",
                    Description = "Specialized reasoning model using reinforcement learning for competitive coding and mathematics.",
                    Icon = "🟢",
                    WebUrl = "https://openai.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "openai.com.png")
                },

                // Anthropic
                new ModelItem
                {
                    Id = "claude-3-5-sonnet-20241022",
                    Name = "Claude 3.5 Sonnet",
                    Provider = "Anthropic",
                    Endpoint = "https://api.anthropic.com/v1/messages",
                    Family = "Frontier Intelligence",
                    Format = "Cloud REST API",
                    Context = "200k",
                    Parameters = "Frontier",
                    Pricing = "$3.00 / $15.00 MTok",
                    Status = "API Ready",
                    Description = "Anthropic state-of-the-art model leading industry benchmarks in advanced reasoning, coding, and tool use.",
                    Icon = "🟠",
                    WebUrl = "https://anthropic.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "anthropic.com.png")
                },
                new ModelItem
                {
                    Id = "claude-3-5-haiku-20241022",
                    Name = "Claude 3.5 Haiku",
                    Provider = "Anthropic",
                    Endpoint = "https://api.anthropic.com/v1/messages",
                    Family = "High-Speed Agentic",
                    Format = "Cloud REST API",
                    Context = "200k",
                    Parameters = "Fast Frontier",
                    Pricing = "$0.80 / $4.00 MTok",
                    Status = "API Ready",
                    Description = "Next-generation fast model matching Claude 3 Opus capabilities at unmatched speed.",
                    Icon = "🟠",
                    WebUrl = "https://anthropic.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "anthropic.com.png")
                },

                // Google Gemini
                new ModelItem
                {
                    Id = "gemini-1.5-pro",
                    Name = "Gemini 1.5 Pro",
                    Provider = "Google Gemini",
                    Endpoint = "https://generativelanguage.googleapis.com/v1beta/models",
                    Family = "Long-Context Pioneer",
                    Format = "Cloud REST API",
                    Context = "2048k",
                    Parameters = "Frontier",
                    Pricing = "$1.25 / $5.00 MTok",
                    Status = "API Ready",
                    Description = "Google DeepMind frontier model with a massive 2-million-token context window and multimodal audio/video mastery.",
                    Icon = "🔵",
                    WebUrl = "https://aistudio.google.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "google.com.png")
                },
                new ModelItem
                {
                    Id = "gemini-1.5-flash",
                    Name = "Gemini 1.5 Flash",
                    Provider = "Google Gemini",
                    Endpoint = "https://generativelanguage.googleapis.com/v1beta/models",
                    Family = "Ultra-Low Latency",
                    Format = "Cloud REST API",
                    Context = "1000k",
                    Parameters = "Lightweight",
                    Pricing = "$0.075 / $0.30 MTok",
                    Status = "API Ready",
                    Description = "Google lightweight high-frequency multimodal model built for real-time applications and rapid answers.",
                    Icon = "🔵",
                    WebUrl = "https://aistudio.google.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "google.com.png")
                },
                new ModelItem
                {
                    Id = "gemini-2.0-flash-exp",
                    Name = "Gemini 2.0 Flash (Experimental)",
                    Provider = "Google Gemini",
                    Endpoint = "https://generativelanguage.googleapis.com/v1beta/models",
                    Family = "Next-Gen Multimodal",
                    Format = "Cloud REST API",
                    Context = "1048k",
                    Parameters = "Experimental Frontier",
                    Pricing = "Free Tier",
                    Status = "API Ready",
                    Description = "Google next-generation experimental model with native multimodal tool use and sub-second generation.",
                    Icon = "🔵",
                    WebUrl = "https://aistudio.google.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "google.com.png")
                },

                // xAI Grok
                new ModelItem
                {
                    Id = "grok-2-1212",
                    Name = "Grok-2",
                    Provider = "xAI (Grok)",
                    Endpoint = "https://api.x.ai/v1/chat/completions",
                    Family = "Truth & Realtime",
                    Format = "Cloud REST API",
                    Context = "128k",
                    Parameters = "Frontier",
                    Pricing = "$2.00 / $10.00 MTok",
                    Status = "API Ready",
                    Description = "xAI frontier conversational model equipped with high-speed coding, math, and logic processing.",
                    Icon = "⚪",
                    WebUrl = "https://x.ai",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "x.ai.png")
                },
                new ModelItem
                {
                    Id = "grok-2-vision-1212",
                    Name = "Grok-2 Vision",
                    Provider = "xAI (Grok)",
                    Endpoint = "https://api.x.ai/v1/chat/completions",
                    Family = "Multimodal Visual",
                    Format = "Cloud REST API",
                    Context = "32k",
                    Parameters = "Vision Frontier",
                    Pricing = "$2.00 / $10.00 MTok",
                    Status = "API Ready",
                    Description = "xAI multimodal reasoning model capable of analyzing diagrams, charts, photos, and UI mockups.",
                    Icon = "⚪",
                    WebUrl = "https://x.ai",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "x.ai.png")
                },

                // DeepSeek
                new ModelItem
                {
                    Id = "deepseek-chat",
                    Name = "DeepSeek-V3",
                    Provider = "DeepSeek",
                    Endpoint = "https://api.deepseek.com/chat/completions",
                    Family = "MoE Architecture",
                    Format = "Cloud REST API",
                    Context = "64k",
                    Parameters = "671B (37B active)",
                    Pricing = "$0.14 / $0.28 MTok",
                    Status = "API Ready",
                    Description = "DeepSeek open-architecture Mixture-of-Experts flagship with lightning-fast code generation and reasoning.",
                    Icon = "🟣",
                    WebUrl = "https://deepseek.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "deepseek.com.png")
                },
                new ModelItem
                {
                    Id = "deepseek-reasoner",
                    Name = "DeepSeek-R1",
                    Provider = "DeepSeek",
                    Endpoint = "https://api.deepseek.com/chat/completions",
                    Family = "Chain-of-Thought",
                    Format = "Cloud REST API",
                    Context = "64k",
                    Parameters = "671B (37B active)",
                    Pricing = "$0.55 / $2.19 MTok",
                    Status = "API Ready",
                    Description = "DeepSeek pure reasoning frontier model utilizing deep reinforcement learning for step-by-step thinking.",
                    Icon = "🟣",
                    WebUrl = "https://deepseek.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "deepseek.com.png")
                },

                // Groq
                new ModelItem
                {
                    Id = "llama-3.3-70b-versatile",
                    Name = "Llama 3.3 70B (Groq LPU)",
                    Provider = "Groq",
                    Endpoint = "https://api.groq.com/openai/v1/chat/completions",
                    Family = "Ultra-Fast LPU",
                    Format = "Cloud REST API",
                    Context = "128k",
                    Parameters = "70B Dense",
                    Pricing = "$0.59 / $0.79 MTok",
                    Status = "API Ready",
                    Description = "Groq Language Processing Unit (LPU) inference delivering ultra-fast 300+ tokens/sec throughput with deterministic latency.",
                    Icon = "⚡",
                    WebUrl = "https://console.groq.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "groq.com.png")
                },

                // OpenRouter — models are fetched live from the provider catalog once a key is set,
                // so no hardcoded entry is shipped here (see CloudApiService.FetchProviderModelsAsync).

                // Cloudflare
                new ModelItem
                {
                    Id = "@cf/meta/llama-3.1-70b-instruct",
                    Name = "Llama 3.1 70B (Workers AI)",
                    Provider = "Cloudflare Workers AI",
                    Endpoint = "https://api.cloudflare.com/client/v4/accounts/{account_id}/ai/v1/chat/completions",
                    Family = "Serverless Edge",
                    Format = "Cloud REST API",
                    Context = "128k",
                    Parameters = "70B Instruct",
                    Pricing = "Serverless Edge",
                    Status = "API Ready",
                    Description = "Cloudflare serverless inference running on GPUs across Cloudflare global 300+ city edge network.",
                    Icon = "☁️",
                    WebUrl = "https://developers.cloudflare.com/workers-ai",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "cloudflare.com.png")
                },

                // Mistral AI
                new ModelItem
                {
                    Id = "mistral-large-latest",
                    Name = "Mistral Large 2",
                    Provider = "Mistral AI",
                    Endpoint = "https://api.mistral.ai/v1/chat/completions",
                    Family = "Multilingual Reasoning",
                    Format = "Cloud REST API",
                    Context = "128k",
                    Parameters = "123B Frontier",
                    Pricing = "$2.00 / $6.00 MTok",
                    Status = "API Ready",
                    Description = "Mistral AI flagship frontier model with world-class reasoning, code synthesis, and native multilingual capabilities.",
                    Icon = "🌪️",
                    WebUrl = "https://console.mistral.ai",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "mistral.ai.png")
                },

                // Hugging Face — models are fetched live from the provider catalog once a key is set,
                // so no hardcoded entry is shipped here (see CloudApiService.FetchProviderModelsAsync).

                // Cohere
                new ModelItem
                {
                    Id = "command-r-plus-08-2024",
                    Name = "Command R+ (Cohere v2)",
                    Provider = "Cohere",
                    Endpoint = "https://api.cohere.com/v2/chat",
                    Family = "Enterprise RAG & Agents",
                    Format = "Cloud REST API",
                    Context = "128k",
                    Parameters = "104B Enterprise",
                    Pricing = "$2.50 / $10.00 MTok",
                    Status = "API Ready",
                    Description = "Enterprise-grade conversational model optimized for complex multi-step reasoning, grounded citations, and RAG.",
                    Icon = "💠",
                    WebUrl = "https://dashboard.cohere.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "cohere.com.png")
                },

                // NVIDIA NIM
                new ModelItem
                {
                    Id = "meta/llama-3.1-70b-instruct",
                    Name = "Llama 3.1 70B (NVIDIA NIM)",
                    Provider = "NVIDIA NIM",
                    Endpoint = "https://integrate.api.nvidia.com/v1/chat/completions",
                    Family = "TensorRT-LLM Accelerated",
                    Format = "Cloud REST API",
                    Context = "128k",
                    Parameters = "70B Optimized",
                    Pricing = "Free Tier / NIM Credits",
                    Status = "API Ready",
                    Description = "NVIDIA Inference Microservice (NIM) accelerated by TensorRT-LLM and Triton on enterprise NVIDIA GPUs.",
                    Icon = "🟢",
                    WebUrl = "https://build.nvidia.com",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "nvidia.com.png")
                },

                // SambaNova Cloud
                new ModelItem
                {
                    Id = "Meta-Llama-3.1-70B-Instruct",
                    Name = "Llama 3.1 70B (SambaNova)",
                    Provider = "SambaNova Cloud",
                    Endpoint = "https://api.sambanova.ai/v1/chat/completions",
                    Family = "Reconfigurable Dataflow",
                    Format = "Cloud REST API",
                    Context = "128k",
                    Parameters = "70B Hardware-Native",
                    Pricing = "Free Tier / Enterprise",
                    Status = "API Ready",
                    Description = "SambaNova Cloud running natively on SN40L Reconfigurable Dataflow Units (RDUs) for ultra-high concurrency.",
                    Icon = "🔷",
                    WebUrl = "https://cloud.sambanova.ai",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "sambanova.ai.png")
                },

                // Zhipu AI
                new ModelItem
                {
                    Id = "glm-4-plus",
                    Name = "GLM-4 Plus (Zhipu AI)",
                    Provider = "Z.AI / Zhipu",
                    Endpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions",
                    Family = "Bilingual Frontier",
                    Format = "Cloud REST API",
                    Context = "128k",
                    Parameters = "Frontier Foundation",
                    Pricing = "¥50 / ¥100 MTok",
                    Status = "API Ready",
                    Description = "Zhipu AI (BigModel) flagship bilingual foundation model with advanced mathematical reasoning and code intelligence.",
                    Icon = "🇨🇳",
                    WebUrl = "https://open.bigmodel.cn",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "bigmodel.cn.png")
                }
            };
        }
    }
}
