using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using PipelinesExplorer.VisualStudio.Resources;

namespace PipelinesExplorer.VisualStudio.ViewModels;

/// <summary>
/// View-model bound to <c>BranchPickerDialog.xaml</c>. Exposes the list of
/// branches available on a repository plus a synthetic "use default branch"
/// option, presented as a vertical, scrollable ComboBox (mirrors the tenant
/// picker UX).
/// </summary>
[DataContract]
public sealed class BranchPickerDialogViewModel : NotifyPropertyChangedObject
{
    private BranchChoice? _selected;

    public BranchPickerDialogViewModel(string repoLabel, IReadOnlyList<string> branches, string? currentBranchOverride)
    {
        Prompt = string.Format(CultureInfo.CurrentCulture, Strings.BranchPicker_Prompt_Format, repoLabel);
        Choices = new ObservableCollection<BranchChoice>();

        // First option: clears any per-repo override and falls back to the
        // repository default branch on Azure DevOps.
        var defaultChoice = new BranchChoice
        {
            Name = null,
            Title = Strings.BranchPicker_UseDefault,
        };
        Choices.Add(defaultChoice);

        foreach (var b in branches)
        {
            Choices.Add(new BranchChoice { Name = b, Title = b });
        }

        // Pre-select the currently active override, falling back to the default.
        if (!string.IsNullOrEmpty(currentBranchOverride))
        {
            foreach (var c in Choices)
            {
                if (string.Equals(c.Name, currentBranchOverride, System.StringComparison.Ordinal))
                {
                    _selected = c;
                    break;
                }
            }
        }
        _selected ??= defaultChoice;
    }

    [DataMember]
    public string Prompt { get; }

    [DataMember]
    public ObservableCollection<BranchChoice> Choices { get; }

    [DataMember]
    public BranchChoice? SelectedChoice
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
}

[DataContract]
public sealed class BranchChoice
{
    /// <summary>
    /// Branch name, or <c>null</c> for the synthetic "use default branch" entry
    /// (which clears the per-repo override).
    /// </summary>
    [DataMember]
    public string? Name { get; set; }

    [DataMember]
    public string Title { get; set; } = string.Empty;
}
