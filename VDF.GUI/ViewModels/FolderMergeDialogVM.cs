using ReactiveUI;
using System.Reactive;

namespace VDF.GUI.ViewModels {
    public sealed record FolderMergeDialogChoice(string SourceFolder, string TargetFolder);

    public sealed class FolderMergeDialogVM : ReactiveObject {
        string sourceFolder, targetFolder, errorText = string.Empty;
        public FolderMergeDialogVM(string first, string second) {
            sourceFolder = first;
            targetFolder = second;
            SwapCommand = ReactiveCommand.Create(() => {
                (SourceFolder, TargetFolder) = (TargetFolder, SourceFolder);
                ErrorText = string.Empty;
            });
        }

        public string SourceFolder {
            get => sourceFolder;
            set => this.RaiseAndSetIfChanged(ref sourceFolder, value);
        }
        public string TargetFolder {
            get => targetFolder;
            set => this.RaiseAndSetIfChanged(ref targetFolder, value);
        }
        public string ErrorText {
            get => errorText;
            set => this.RaiseAndSetIfChanged(ref errorText, value);
        }
        public ReactiveCommand<Unit, Unit> SwapCommand { get; }
    }
}
