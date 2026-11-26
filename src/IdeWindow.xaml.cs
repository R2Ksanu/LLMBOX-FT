using System.Windows;
using System.Windows.Input;
using LlmodelBox.Controls;
using LlmodelBox.Models;
using LlmodelBox.Services;

namespace LlmodelBox
{
    public partial class IdeWindow : Window
    {
        private readonly ProjectItem _project;

        public WorkspaceControl Workspace { get; }
        public string ProjectPath => _project.Path;

        public IdeWindow(ProjectItem project, WorkspaceControl workspace, string? theme, string? accent)
        {
            _project = project;
            InitializeComponent();
            ThemeManager.ApplyTheme(theme, Resources, accent);

            Workspace = workspace;
            WorkspaceHost.Content = workspace;

            Title = $"LlmodelBox IDE — {project.Name}";
            TxtTitle.Text = project.Name;

            Loaded += (_, __) => Workspace.LoadProject(_project);
        }

        public void SetStatusModel(string model) => TxtStatusModel.Text = string.IsNullOrWhiteSpace(model) ? "—" : model;
        public void SetStatusTokens(string tokens) => TxtStatusTokens.Text = string.IsNullOrWhiteSpace(tokens) ? "0" : tokens;
        public void SetStatusConnection(string state) => TxtStatusConnection.Text = string.IsNullOrWhiteSpace(state) ? "Ready" : state;

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                ToggleMaximize();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                e.Handled = true;
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void IdeWindow_StateChanged(object? sender, System.EventArgs e)
        {
            if (RootBorder != null)
            {
                RootBorder.Padding = WindowState == WindowState.Maximized
                    ? new Thickness(7)
                    : new Thickness(0);
            }
        }
    }
}
