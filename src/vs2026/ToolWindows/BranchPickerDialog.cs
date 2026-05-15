using Microsoft.VisualStudio.Extensibility.UI;
using PipelinesExplorer.VisualStudio.ViewModels;

namespace PipelinesExplorer.VisualStudio.ToolWindows;

/// <summary>
/// Modal dialog used to pick a branch for a repository. The XAML counterpart
/// is shipped as an embedded resource (see
/// <c>PipelinesExplorer.VisualStudio.csproj</c>).
/// </summary>
internal sealed class BranchPickerDialog : RemoteUserControl
{
    public BranchPickerDialog(BranchPickerDialogViewModel viewModel)
        : base(viewModel)
    {
    }
}
