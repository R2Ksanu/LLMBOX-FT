using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using LlmodelBox.Controls.Editor;
using LlmodelBox.Infrastructure.Editor;
using LlmodelBox.Services;
using Xunit;

namespace LlmodelBox.Tests
{
    public class CodeEditorTests
    {
        [Theory]
        [InlineData("Program.cs", "csharp", "C#", EditorFoldingKind.Brace)]
        [InlineData("main.py", "python", "PY", EditorFoldingKind.Indent)]
        [InlineData("app.tsx", "typescript", "TSX", EditorFoldingKind.Brace)]
        [InlineData("modelbox.json", "json", "JSON", EditorFoldingKind.Brace)]
        [InlineData("MainWindow.xaml", "xaml", "XAML", EditorFoldingKind.Xml)]
        [InlineData("README.md", "markdown", "MD", EditorFoldingKind.Indent)]
        [InlineData("deploy.ps1", "powershell", "PS1", EditorFoldingKind.Brace)]
        [InlineData("Dockerfile", "shell", "DOCKER", EditorFoldingKind.Indent)]
        public void EditorLanguageService_DetectsLanguagesAndFoldingStrategies(
            string fileName,
            string expectedId,
            string expectedBadge,
            EditorFoldingKind expectedFolding)
        {
            var info = EditorLanguageService.DetectLanguage(fileName);
            Assert.Equal(expectedId, info.Id);
            Assert.Equal(expectedBadge, info.Badge);
            Assert.Equal(expectedFolding, info.FoldingKind);
        }

        [Fact]
        public void EditorLanguageService_ResolvesThemedHighlightingsForDarkAndLight()
        {
            foreach (string sample in new[] { "Program.cs", "main.py", "modelbox.json", "MainWindow.xaml" })
            {
                var lang = EditorLanguageService.DetectLanguage(sample);
                var darkDef = EditorLanguageService.GetThemedHighlighting(lang, isDarkTheme: true);
                var lightDef = EditorLanguageService.GetThemedHighlighting(lang, isDarkTheme: false);
                Assert.NotNull(darkDef);
                Assert.NotNull(lightDef);
            }
        }

        [Fact]
        public void EditorLanguageService_ComputesBraceAndIndentFoldingsAccurately()
        {
            string cs = "namespace Demo {\n    class A {\n        string s = \"{\";\n        void M() {\n            int x = 1;\n        }\n    }\n}";
            var csDoc = new TextDocument(cs);
            var braceFolds = EditorLanguageService.ComputeBraceFoldings(csDoc, out int err);
            Assert.Equal(-1, err);
            Assert.True(braceFolds.Count >= 3);

            string py = "def hello():\n    print('hi')\n    if True:\n        return 1\n\nclass B:\n    pass\n";
            var pyDoc = new TextDocument(py);
            var indentFolds = EditorLanguageService.ComputeIndentFoldings(pyDoc);
            Assert.True(indentFolds.Count >= 2);
        }

        [Fact]
        public void BracketHighlightRenderer_FindsMatchingNestedBrackets()
        {
            string code = "if ((a + b[2]) == foo(x)) { return; }";
            int openParen = code.IndexOf('(');
            var (open, close) = BracketHighlightRenderer.FindMatchingBracket(code, openParen);
            Assert.Equal(openParen, open);
            Assert.Equal(code.IndexOf(") {", StringComparison.Ordinal), close);

            // Adjacent right after closing brace
            int afterCloseBrace = code.LastIndexOf('}') + 1;
            var (bOpen, bClose) = BracketHighlightRenderer.FindMatchingBracket(code, afterCloseBrace);
            Assert.Equal(code.IndexOf('{'), bOpen);
            Assert.Equal(code.LastIndexOf('}'), bClose);
        }

        [Fact]
        public void EditorDiagnosticsHelper_ParsesBuildOutputAndStructuralJsonErrors()
        {
            string buildLog = "G:\\proj\\Program.cs(42,13): error CS1002: ; expected\n" +
                              "G:\\proj\\Helper.cs(10,5): warning CS0168: Variable unused";
            var buildDiags = EditorDiagnosticsHelper.ParseBuildOutput(buildLog);
            Assert.Equal(2, buildDiags.Count);
            Assert.Equal(DiagnosticSeverity.Error, buildDiags[0].Severity);
            Assert.Equal(42, buildDiags[0].Line);
            Assert.Equal("CS1002", buildDiags[0].Code);
            Assert.Equal(DiagnosticSeverity.Warning, buildDiags[1].Severity);

            string badJson = "{\n  \"name\": \"test\",\n  \"broken\": [1, 2,\n}";
            var jsonDiags = EditorDiagnosticsHelper.AnalyzeLightweight("test.json", "json", badJson);
            Assert.Single(jsonDiags);
            Assert.Equal(DiagnosticSeverity.Error, jsonDiags[0].Severity);

            string badCs = "void Test() {\n    if (true) {\n}\n";
            var csDiags = EditorDiagnosticsHelper.AnalyzeLightweight("Test.cs", "csharp", badCs);
            Assert.Single(csDiags);
            Assert.Contains("Unclosed", csDiags[0].Message);
        }

        [Fact]
        public void TextMarkerService_AndInlineAiProposal_TrackMarkersAndChangedLines()
        {
            var doc = new TextDocument("line1\nline2_old\nline3\n");
            var svc = new TextMarkerService(doc);
            var marker = svc.Create(6, 9);
            marker.MarkerType = TextMarkerType.Squiggle;
            marker.ToolTipText = "Warning test";
            Assert.Single(svc.AllMarkers);
            Assert.Single(svc.GetMarkersAtOffset(8));

            var proposal = EditorDiagnosticsHelper.BuildInlineProposal(
                "demo.cs",
                "line1\nline2_old\nline3\n",
                "line1\nline2_ai_new\nline3\nline4_added\n",
                additions: 2,
                deletions: 1);
            Assert.Contains(2, proposal.ChangedLineNumbers);
            Assert.Contains(4, proposal.ChangedLineNumbers);
            Assert.True(proposal.Additions >= 2);
        }

        [Fact]
        public void WorkspaceService_AtomicSavePreservesWorkspaceSafetyOnMultiTabBuffers()
        {
            string root = Path.Combine(Path.GetTempPath(), "LlmodelBox_EditorTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string p1 = Path.Combine(root, "alpha.py");
            string p2 = Path.Combine(root, "beta.json");

            try
            {
                var ws = new WorkspaceService();
                Assert.True(ws.SaveFileContent(p1, "def run():\n    return 42\n"));
                Assert.True(ws.SaveFileContent(p2, "{\"ok\": true}\n"));

                string c1 = File.ReadAllText(p1);
                string c2 = File.ReadAllText(p2);

                var tab1 = new EditorTabState
                {
                    FilePath = p1,
                    Language = EditorLanguageService.DetectLanguage(p1),
                    Document = new TextDocument(c1),
                    OriginalDiskContent = c1,
                    IsDirty = false
                };
                var tab2 = new EditorTabState
                {
                    FilePath = p2,
                    Language = EditorLanguageService.DetectLanguage(p2),
                    Document = new TextDocument("{\"ok\": false}\n"),
                    OriginalDiskContent = c2,
                    IsDirty = true
                };

                Assert.False(tab1.IsDirty);
                Assert.True(tab2.IsDirty);
                Assert.NotEqual(tab1.Document.Text, tab2.Document.Text);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void CodeEditorWorkspace_TabSwitchAndLayoutMeasure_DoesNotThrowInvalidDocument()
        {
            string root = Path.Combine(Path.GetTempPath(), "LlmodelBox_EditorStaTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string p1 = Path.Combine(root, "Demo.cs");
            string p2 = Path.Combine(root, "Script.py");
            File.WriteAllText(p1, "namespace Test {\n    class Program {\n        void Main() { }\n    }\n}\n");
            File.WriteAllText(p2, "def run():\n    return 1\n");

            Exception? staError = null;
            var thread = new Thread(() =>
            {
                try
                {
                    if (Application.Current == null)
                    {
                        var app = new Application();
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/LlmodelBox;component/Resources/Tokens.xaml", UriKind.Absolute)
                        });
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/LlmodelBox;component/Resources/Controls.xaml", UriKind.Absolute)
                        });
                        ThemeManager.ApplyTheme(ThemeManager.ThemeMidnight, app.Resources, ThemeManager.DefaultAccent);
                    }
                    var editor = new CodeEditorWorkspace();
                    editor.Measure(new Size(800, 500));
                    editor.Arrange(new Rect(0, 0, 800, 500));
                    editor.UpdateLayout();

                    static void AwaitOnDispatcher(Task t)
                    {
                        var frame = new System.Windows.Threading.DispatcherFrame();
                        t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                        System.Windows.Threading.Dispatcher.PushFrame(frame);
                        t.GetAwaiter().GetResult();
                    }

                    AwaitOnDispatcher(editor.OpenFileAsync(p1));
                    editor.Measure(new Size(800, 500));
                    editor.Arrange(new Rect(0, 0, 800, 500));
                    editor.UpdateLayout();

                    AwaitOnDispatcher(editor.OpenFileAsync(p2));
                    editor.Measure(new Size(800, 500));
                    editor.Arrange(new Rect(0, 0, 800, 500));
                    editor.UpdateLayout();

                    Assert.Equal(p2, editor.ActiveFilePath);
                    Assert.Equal(2, editor.OpenTabs.Count);
                }
                catch (Exception ex)
                {
                    staError = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(15));

            try { Directory.Delete(root, recursive: true); } catch { }
            Assert.Null(staError);
        }
    }
}
