using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using LlmodelBox.Models;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LlmodelBox.Runtime.Content
{
    /// <summary>
    /// Converts Markdown and structured fenced/JSON blocks into typed ChatContentBlock items
    /// using Markdig's AST. Safely handles incomplete streaming Markdown and falls back
    /// gracefully to MarkdownBlock/CodeBlock on malformed structured payloads.
    /// </summary>
    public static class StructuredContentParser
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseTaskLists()
            .Build();

        public static List<ChatContentBlock> Parse(string? rawText, IEnumerable<ChatContentBlock>? extraBlocks = null)
        {
            var result = new List<ChatContentBlock>();
            string text = (rawText ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");

            if (!string.IsNullOrWhiteSpace(text))
            {
                var doc = Markdown.Parse(text, Pipeline);
                var lines = text.Split('\n');
                var mdBuffer = new StringBuilder();

                void FlushMarkdown()
                {
                    if (mdBuffer.Length == 0) return;
                    string chunk = mdBuffer.ToString().Trim();
                    mdBuffer.Clear();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        result.Add(new MarkdownBlock { Text = chunk });
                    }
                }

                foreach (var block in doc)
                {
                    if (block is FencedCodeBlock fenced)
                    {
                        FlushMarkdown();
                        string lang = (fenced.Info ?? string.Empty).Trim();
                        string code = ExtractCodeText(fenced);
                        result.Add(ParseFencedBlock(lang, code));
                    }
                    else if (block is Markdig.Syntax.CodeBlock indented)
                    {
                        FlushMarkdown();
                        result.Add(new LlmodelBox.Models.CodeBlock { Language = string.Empty, Code = ExtractCodeText(indented) });
                    }
                    else if (block is Table table)
                    {
                        FlushMarkdown();
                        result.Add(ParseTableBlock(table));
                    }
                    else if (block is ListBlock list && IsTaskChecklist(list))
                    {
                        FlushMarkdown();
                        result.Add(ParseTaskListBlock(list));
                    }
                    else
                    {
                        string slice = SliceLines(lines, block.Line, GetLastLine(block, lines.Length - 1));
                        if (!string.IsNullOrWhiteSpace(slice))
                        {
                            if (mdBuffer.Length > 0) mdBuffer.AppendLine();
                            mdBuffer.Append(slice);
                        }
                    }
                }

                FlushMarkdown();
            }

            if (extraBlocks != null)
            {
                foreach (var b in extraBlocks)
                {
                    if (b != null) result.Add(b);
                }
            }

            return result;
        }

        private static ChatContentBlock ParseFencedBlock(string lang, string code)
        {
            string lower = lang.ToLowerInvariant();
            if (lower == "mermaid")
            {
                return new MermaidBlock
                {
                    DiagramType = DetectMermaidType(code),
                    Source = code
                };
            }

            if (lower is "chart" or "llmodelbox-chart" or "json-chart")
            {
                if (TryParseChartJson(code, out var chart)) return chart;
            }

            if (lower is "tasklist" or "llmodelbox-tasks" or "tasks")
            {
                if (TryParseTaskListJson(code, out var tl)) return tl;
            }

            if (lower is "citations" or "sources")
            {
                if (TryParseCitationJson(code, out var cit)) return cit;
            }

            if (lower is "progress" or "llmodelbox-progress")
            {
                if (TryParseProgressJson(code, out var prog)) return prog;
            }

            if (lower is "file" or "attachment")
            {
                if (TryParseFileJson(code, out var file)) return file;
            }

            if (lower is "diff" or "patch")
            {
                return ParseDiffBlock(code);
            }

            return new LlmodelBox.Models.CodeBlock
            {
                Language = lang,
                Code = code
            };
        }

        private static string DetectMermaidType(string source)
        {
            foreach (var raw in source.Split('\n'))
            {
                string t = raw.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(t) || t.StartsWith("%%")) continue;
                if (t.StartsWith("sequencediagram")) return "sequenceDiagram";
                if (t.StartsWith("classdiagram")) return "classDiagram";
                if (t.StartsWith("statediagram")) return "stateDiagram";
                if (t.StartsWith("erdiagram")) return "erDiagram";
                if (t.StartsWith("gantt")) return "gantt";
                if (t.StartsWith("timeline")) return "timeline";
                if (t.StartsWith("journey")) return "journey";
                if (t.StartsWith("flowchart") || t.StartsWith("graph")) return "flowchart";
                break;
            }
            return "flowchart";
        }

        private static bool IsTaskChecklist(ListBlock list)
        {
            foreach (var item in list.OfType<ListItemBlock>())
            {
                foreach (var child in item)
                {
                    if (child is ParagraphBlock pb && pb.Inline != null)
                    {
                        foreach (var inline in pb.Inline)
                        {
                            if (inline is TaskList) return true;
                        }
                    }
                }
            }
            return false;
        }

        private static TaskListBlock ParseTaskListBlock(ListBlock list)
        {
            var block = new TaskListBlock { Title = "Checklist" };
            foreach (var item in list.OfType<ListItemBlock>())
            {
                bool checkedState = false;
                var textParts = new List<string>();
                foreach (var child in item)
                {
                    if (child is ParagraphBlock pb && pb.Inline != null)
                    {
                        foreach (var inline in pb.Inline)
                        {
                            if (inline is TaskList tl) checkedState = tl.Checked;
                            else textParts.Add(ExtractInlinePlainText(inline));
                        }
                    }
                }
                string title = string.Join("", textParts).Trim();
                if (!string.IsNullOrEmpty(title))
                {
                    block.Tasks.Add(new TaskChecklistItem { Title = title, IsCompleted = checkedState });
                }
            }
            return block;
        }

        private static TableBlock ParseTableBlock(Table table)
        {
            var tb = new TableBlock();
            bool headerRead = false;
            foreach (var rowObj in table)
            {
                if (rowObj is not TableRow row) continue;
                var cells = new List<string>();
                foreach (var cellObj in row)
                {
                    if (cellObj is not TableCell cell) continue;
                    var cellText = new StringBuilder();
                    foreach (var child in cell)
                    {
                        if (child is ParagraphBlock pb && pb.Inline != null)
                        {
                            if (cellText.Length > 0) cellText.Append(' ');
                            foreach (var inl in pb.Inline) cellText.Append(ExtractInlinePlainText(inl));
                        }
                    }
                    cells.Add(cellText.ToString().Trim());
                }

                if (row.IsHeader && !headerRead)
                {
                    tb.Headers.AddRange(cells);
                    headerRead = true;
                }
                else
                {
                    tb.Rows.Add(cells);
                }
            }
            if (!headerRead && tb.Rows.Count > 0)
            {
                tb.Headers.AddRange(tb.Rows[0]);
                tb.Rows.RemoveAt(0);
            }
            return tb;
        }

        private static CodeDiffBlock ParseDiffBlock(string patch)
        {
            var lines = patch.Split('\n');
            string filePath = string.Empty;
            var before = new StringBuilder();
            var after = new StringBuilder();
            int adds = 0, dels = 0;

            foreach (var line in lines)
            {
                if (line.StartsWith("+++ b/")) { filePath = line.Substring(6).Trim(); continue; }
                if (line.StartsWith("+++ ")) { filePath = line.Substring(4).Trim(); continue; }
                if (line.StartsWith("--- ") || line.StartsWith("@@") || line.StartsWith("diff --git") || line.StartsWith("index ")) continue;
                if (line.StartsWith("+"))
                {
                    adds++;
                    after.AppendLine(line.Substring(1));
                }
                else if (line.StartsWith("-"))
                {
                    dels++;
                    before.AppendLine(line.Substring(1));
                }
                else
                {
                    string plain = line.StartsWith(" ") ? line.Substring(1) : line;
                    before.AppendLine(plain);
                    after.AppendLine(plain);
                }
            }

            return new CodeDiffBlock
            {
                FilePath = filePath,
                BeforeContent = before.ToString().TrimEnd('\n'),
                AfterContent = after.ToString().TrimEnd('\n'),
                RawPatch = patch,
                Additions = adds,
                Deletions = dels
            };
        }

        private static bool TryParseChartJson(string json, out ChartBlock chart)
        {
            chart = new ChartBlock();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                string typeStr = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "bar")
                    : (root.TryGetProperty("kind", out var k) ? (k.GetString() ?? "bar") : "bar");
                chart.Kind = typeStr.Trim().ToLowerInvariant() switch
                {
                    "line" => ChartKind.Line,
                    "horizontalbar" or "horizontal_bar" or "hbar" => ChartKind.HorizontalBar,
                    "pie" => ChartKind.Pie,
                    "donut" or "doughnut" => ChartKind.Donut,
                    "area" => ChartKind.Area,
                    "scatter" => ChartKind.Scatter,
                    _ => ChartKind.Bar
                };
                if (root.TryGetProperty("title", out var title)) chart.Title = title.GetString() ?? "";
                if (root.TryGetProperty("xLabel", out var xl)) chart.XLabel = xl.GetString() ?? "";
                if (root.TryGetProperty("yLabel", out var yl)) chart.YLabel = yl.GetString() ?? "";

                if (root.TryGetProperty("series", out var seriesArr) && seriesArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in seriesArr.EnumerateArray())
                    {
                        var cs = new ChartSeries
                        {
                            Name = s.TryGetProperty("name", out var sn) ? (sn.GetString() ?? "Series") : "Series",
                            ColorHex = s.TryGetProperty("color", out var sc) ? (sc.GetString() ?? "") : ""
                        };
                        if (s.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Array)
                        {
                            int idx = 0;
                            foreach (var pt in pts.EnumerateArray())
                            {
                                cs.Points.Add(ParseChartPoint(pt, idx++));
                            }
                        }
                        else if (s.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                        {
                            string[]? labels = root.TryGetProperty("labels", out var lbls) && lbls.ValueKind == JsonValueKind.Array
                                ? lbls.EnumerateArray().Select(l => l.ToString()).ToArray()
                                : null;
                            int idx = 0;
                            foreach (var v in vals.EnumerateArray())
                            {
                                double y = v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                                string lbl = labels != null && idx < labels.Length ? labels[idx] : $"#{idx + 1}";
                                cs.Points.Add(new ChartDataPoint { Label = lbl, X = idx, Y = y });
                                idx++;
                            }
                        }
                        if (cs.Points.Count > 0) chart.Series.Add(cs);
                    }
                }
                else if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    var cs = new ChartSeries { Name = chart.Title.Length > 0 ? chart.Title : "Data" };
                    int idx = 0;
                    foreach (var pt in dataArr.EnumerateArray()) cs.Points.Add(ParseChartPoint(pt, idx++));
                    if (cs.Points.Count > 0) chart.Series.Add(cs);
                }

                return chart.Series.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static ChartDataPoint ParseChartPoint(JsonElement pt, int index)
        {
            if (pt.ValueKind == JsonValueKind.Number)
            {
                return new ChartDataPoint { Label = $"#{index + 1}", X = index, Y = pt.GetDouble() };
            }
            string lbl = pt.TryGetProperty("label", out var l) ? (l.GetString() ?? l.ToString())
                : (pt.TryGetProperty("name", out var n) ? (n.GetString() ?? n.ToString()) : $"#{index + 1}");
            double x = pt.TryGetProperty("x", out var xv) && xv.ValueKind == JsonValueKind.Number ? xv.GetDouble() : index;
            double y = pt.TryGetProperty("y", out var yv) && yv.ValueKind == JsonValueKind.Number ? yv.GetDouble()
                : (pt.TryGetProperty("value", out var vv) && vv.ValueKind == JsonValueKind.Number ? vv.GetDouble() : 0);
            return new ChartDataPoint { Label = lbl, X = x, Y = y };
        }

        private static bool TryParseTaskListJson(string json, out TaskListBlock block)
        {
            block = new TaskListBlock();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("title", out var t)) block.Title = t.GetString() ?? "Tasks";
                if (root.TryGetProperty("tasks", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        block.Tasks.Add(new TaskChecklistItem
                        {
                            Title = el.TryGetProperty("title", out var tt) ? (tt.GetString() ?? "") : "",
                            Description = el.TryGetProperty("description", out var dd) ? (dd.GetString() ?? "") : "",
                            IsCompleted = el.TryGetProperty("completed", out var cc) && cc.ValueKind == JsonValueKind.True
                        });
                    }
                }
                return block.Tasks.Count > 0;
            }
            catch { return false; }
        }

        private static bool TryParseCitationJson(string json, out CitationBlock block)
        {
            block = new CitationBlock();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                JsonElement arr;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    arr = root;
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("title", out var t)) block.Title = t.GetString() ?? "Sources";
                    if (!root.TryGetProperty("sources", out arr) || arr.ValueKind != JsonValueKind.Array)
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }

                foreach (var el in arr.EnumerateArray())
                {
                    block.Sources.Add(new CitationSourceItem
                    {
                        Title = el.TryGetProperty("title", out var tt) ? (tt.GetString() ?? "") : "",
                        Domain = el.TryGetProperty("domain", out var dm) ? (dm.GetString() ?? "") : "",
                        Description = el.TryGetProperty("description", out var ds) ? (ds.GetString() ?? "") : "",
                        Url = el.TryGetProperty("url", out var uu) ? (uu.GetString() ?? "") : ""
                    });
                }
                return block.Sources.Count > 0;
            }
            catch { return false; }
        }

        private static bool TryParseProgressJson(string json, out ProgressBlock block)
        {
            block = new ProgressBlock();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.TryGetProperty("title", out var t)) block.Title = t.GetString() ?? "Progress";
                if (r.TryGetProperty("currentOperation", out var op)) block.CurrentOperation = op.GetString() ?? "";
                if (r.TryGetProperty("completed", out var cp) && cp.ValueKind == JsonValueKind.Number) block.CompletedOperations = cp.GetInt32();
                if (r.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number) block.TotalOperations = tot.GetInt32();
                if (r.TryGetProperty("failed", out var fl) && fl.ValueKind == JsonValueKind.Number) block.FailedOperations = fl.GetInt32();
                if (r.TryGetProperty("status", out var st)) block.Status = st.GetString() ?? "Running";
                return true;
            }
            catch { return false; }
        }

        private static bool TryParseFileJson(string json, out FileAttachmentBlock block)
        {
            block = new FileAttachmentBlock();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                block.FileName = r.TryGetProperty("fileName", out var fn) ? (fn.GetString() ?? "") : "";
                block.FullPath = r.TryGetProperty("fullPath", out var fp) ? (fp.GetString() ?? "") : "";
                block.Extension = r.TryGetProperty("extension", out var ex) ? (ex.GetString() ?? "") : System.IO.Path.GetExtension(block.FileName);
                block.SizeBytes = r.TryGetProperty("sizeBytes", out var sb) && sb.ValueKind == JsonValueKind.Number ? sb.GetInt64() : 0;
                block.Description = r.TryGetProperty("description", out var ds) ? (ds.GetString() ?? "") : "";
                block.PreviewText = r.TryGetProperty("preview", out var pv) ? (pv.GetString() ?? "") : "";
                return !string.IsNullOrWhiteSpace(block.FileName);
            }
            catch { return false; }
        }

        private static string ExtractCodeText(LeafBlock codeBlock)
        {
            var sb = new StringBuilder();
            var lines = codeBlock.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(lines.Lines[i].Slice.ToString());
            }
            return sb.ToString();
        }

        private static string ExtractInlinePlainText(Inline inline)
        {
            return inline switch
            {
                LiteralInline lit => lit.Content.ToString(),
                CodeInline code => $"`{code.Content}`",
                LineBreakInline => " ",
                ContainerInline container => string.Concat(container.Select(ExtractInlinePlainText)),
                _ => string.Empty
            };
        }

        private static int GetLastLine(MarkdownObject obj, int maxLine)
        {
            int last = obj.Line;
            if (obj is ContainerBlock cb)
            {
                foreach (var child in cb)
                {
                    int cLast = GetLastLine(child, maxLine);
                    if (cLast > last) last = cLast;
                }
            }
            return Math.Min(maxLine, Math.Max(obj.Line, last));
        }

        private static string SliceLines(string[] lines, int startLine, int endLine)
        {
            if (startLine < 0 || startLine >= lines.Length) return string.Empty;
            int end = Math.Min(lines.Length - 1, Math.Max(startLine, endLine));
            var sb = new StringBuilder();
            for (int i = startLine; i <= end; i++)
            {
                if (i > startLine) sb.Append('\n');
                sb.Append(lines[i]);
            }
            return sb.ToString();
        }
    }
}
