using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Views {
    public partial class FolderMergeDialog : Window {
        public FolderMergeDialog(string firstFolder, string secondFolder) {
            AvaloniaXamlLoader.Load(this);
            Owner = ApplicationHelpers.MainWindow;
            if (!VDF.GUI.Data.SettingsFile.Instance.DarkMode)
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            DataContext = new FolderMergeDialogVM(firstFolder, secondFolder);
        }

        void Confirm_Click(object? sender, RoutedEventArgs e) {
            if (DataContext is not FolderMergeDialogVM vm) return;
            string from = (vm.SourceFolder ?? string.Empty).Trim().Trim('"');
            string to = (vm.TargetFolder ?? string.Empty).Trim().Trim('"');
            if (from.Length == 0 || to.Length == 0 ||
                !Path.IsPathFullyQualified(from) || !Path.IsPathFullyQualified(to)) {
                vm.ErrorText = "请填写两个完整的绝对目录路径。";
                return;
            }
            Close(new FolderMergeDialogChoice(from, to));
        }

        void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
    }
}
