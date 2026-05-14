using Microsoft.VisualStudio.Extensibility.UI;
using PipelinesExplorer.VisualStudio.ViewModels;

namespace PipelinesExplorer.VisualStudio.ToolWindows;

/// <summary>
/// RemoteUserControl that hosts the Pipelines tool window XAML. The XAML
/// counterpart lives in <c>PipelinesToolWindowControl.xaml</c> as an
/// embedded resource (see <c>PipelinesExplorer.VisualStudio.csproj</c>).
/// </summary>
internal sealed class PipelinesToolWindowControl : RemoteUserControl
{
    public PipelinesToolWindowControl(PipelinesViewModel viewModel)
        : base(viewModel)
    {
    }
}
