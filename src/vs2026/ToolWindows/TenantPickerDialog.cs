using Microsoft.VisualStudio.Extensibility.UI;
using PipelinesExplorer.VisualStudio.ViewModels;

namespace PipelinesExplorer.VisualStudio.ToolWindows;

/// <summary>
/// Modal dialog used to switch the active Microsoft Entra tenant. The XAML
/// counterpart is shipped as an embedded resource (see
/// <c>PipelinesExplorer.VisualStudio.csproj</c>).
/// </summary>
internal sealed class TenantPickerDialog : RemoteUserControl
{
    public TenantPickerDialog(TenantPickerDialogViewModel viewModel)
        : base(viewModel)
    {
    }
}
