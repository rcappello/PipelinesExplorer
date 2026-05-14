using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using PipelinesExplorer.VisualStudio.Auth;

namespace PipelinesExplorer.VisualStudio.ViewModels;

/// <summary>
/// View-model bound to <c>TenantPickerDialog.xaml</c>. Exposes the list of
/// available Microsoft Entra tenants and the user's current selection so the
/// dialog can present them as a vertical, scrollable ComboBox.
/// </summary>
[DataContract]
public sealed class TenantPickerDialogViewModel : NotifyPropertyChangedObject
{
    private TenantChoice? _selected;

    public TenantPickerDialogViewModel(IReadOnlyList<TenantInfo> tenants, string? currentTenantId)
    {
        Choices = new ObservableCollection<TenantChoice>();

        // First option always represents "no override" (home tenant).
        var defaultChoice = new TenantChoice
        {
            TenantId = null,
            Title = "Default tenant",
            Subtitle = "Use the home tenant of the signed-in account",
        };
        Choices.Add(defaultChoice);

        foreach (var t in tenants)
        {
            Choices.Add(new TenantChoice
            {
                TenantId = t.TenantId,
                Title = t.DisplayName,
                Subtitle = string.IsNullOrEmpty(t.DefaultDomain)
                    ? t.TenantId
                    : $"{t.DefaultDomain}  \u2022  {t.TenantId}",
            });
        }

        // Pre-select the currently active tenant, falling back to "Default tenant".
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            foreach (var c in Choices)
            {
                if (string.Equals(c.TenantId, currentTenantId, System.StringComparison.OrdinalIgnoreCase))
                {
                    _selected = c;
                    break;
                }
            }
        }
        _selected ??= defaultChoice;
    }

    [DataMember]
    public ObservableCollection<TenantChoice> Choices { get; }

    [DataMember]
    public TenantChoice? SelectedChoice
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
}

[DataContract]
public sealed class TenantChoice
{
    [DataMember]
    public string? TenantId { get; set; }

    [DataMember]
    public string Title { get; set; } = string.Empty;

    [DataMember]
    public string? Subtitle { get; set; }
}
