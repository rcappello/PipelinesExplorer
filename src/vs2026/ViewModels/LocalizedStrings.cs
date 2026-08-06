using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using PipelinesExplorer.VisualStudio.Resources;

namespace PipelinesExplorer.VisualStudio.ViewModels;

/// <summary>
/// Snapshot of every localized string referenced by Remote UI XAML, exposed
/// as <see cref="DataMember"/> properties so they can be reached via plain
/// <c>{Binding Strings.SomeKey}</c> in XAML.
/// <para>
/// Remote UI XAML is parsed by the Visual Studio IDE process, which does NOT
/// have our extension assembly loaded — so <c>{x:Static loc:Strings.X}</c>
/// against <see cref="Strings"/> fails with
/// "Type reference cannot find type named ...". Routing the strings through
/// the data context is the documented Remote UI pattern.
/// </para>
/// <para>
/// Values are captured at construction time using the current UI culture.
/// The dialog/tool-window view-models build a fresh instance on each show,
/// so a Visual Studio language change between sessions is picked up
/// automatically.
/// </para>
/// </summary>
[DataContract]
public sealed class LocalizedStrings : NotifyPropertyChangedObject
{
    // Toolbar
    [DataMember] public string Toolbar_Refresh_Tooltip { get; } = Strings.Toolbar_Refresh_Tooltip;
    [DataMember] public string Toolbar_SwitchTenant_Tooltip { get; } = Strings.Toolbar_SwitchTenant_Tooltip;
    [DataMember] public string Toolbar_SignOut_Tooltip { get; } = Strings.Toolbar_SignOut_Tooltip;

    // Welcome / signed-out panel
    [DataMember] public string Welcome_SignInPrompt { get; } = Strings.Welcome_SignInPrompt;
    [DataMember] public string Welcome_SignInWithMicrosoft { get; } = Strings.Welcome_SignInWithMicrosoft;
    [DataMember] public string Welcome_PatHelp { get; } = Strings.Welcome_PatHelp;
    [DataMember] public string Welcome_SignInWithPat { get; } = Strings.Welcome_SignInWithPat;
    [DataMember] public string Welcome_PatField_Tooltip { get; } = Strings.Welcome_PatField_Tooltip;

    // Context menu
    [DataMember] public string Context_Open { get; } = Strings.Context_Open;
    [DataMember] public string Context_LinkWorkspace { get; } = Strings.Context_LinkWorkspace;
    [DataMember] public string Context_UnlinkWorkspace { get; } = Strings.Context_UnlinkWorkspace;
    [DataMember] public string Context_SelectBranch { get; } = Strings.Context_SelectBranch;

    // Tenant picker
    [DataMember] public string TenantPicker_Prompt { get; } = Strings.TenantPicker_Prompt;

    // Accessibility
    [DataMember] public string A11y_PatField_Name { get; } = Strings.A11y_PatField_Name;
    [DataMember] public string A11y_Tree_Name { get; } = Strings.A11y_Tree_Name;
    [DataMember] public string A11y_BranchPicker_ComboBox_Name { get; } = Strings.A11y_BranchPicker_ComboBox_Name;
    [DataMember] public string A11y_TenantPicker_ComboBox_Name { get; } = Strings.A11y_TenantPicker_ComboBox_Name;
    [DataMember] public string A11y_Filter_Name { get; } = Strings.A11y_Filter_Name;

    // Filter
    [DataMember] public string Filter_Placeholder { get; } = Strings.Filter_Placeholder;
    [DataMember] public string Filter_Clear_Tooltip { get; } = Strings.Filter_Clear_Tooltip;
    [DataMember] public string Filter_LoadedScope_Tooltip { get; } = Strings.Filter_LoadedScope_Tooltip;

    // Add-organization panel (plan 002 phase D)
    [DataMember] public string AddOrg_Header { get; } = Strings.AddOrg_Header;
    [DataMember] public string AddOrg_DeprecationNotice { get; } = Strings.AddOrg_DeprecationNotice;
    [DataMember] public string AddOrg_OrgLabel { get; } = Strings.AddOrg_OrgLabel;
    [DataMember] public string AddOrg_OrgHint { get; } = Strings.AddOrg_OrgHint;
    [DataMember] public string AddOrg_OrgFieldHelp { get; } = Strings.AddOrg_OrgFieldHelp;
    [DataMember] public string AddOrg_PatLabel { get; } = Strings.AddOrg_PatLabel;
    [DataMember] public string AddOrg_PatFieldHelp { get; } = Strings.AddOrg_PatFieldHelp;
    [DataMember] public string AddOrg_HistoryLabel { get; } = Strings.AddOrg_HistoryLabel;
    [DataMember] public string AddOrg_Verify { get; } = Strings.AddOrg_Verify;
    [DataMember] public string AddOrg_Cancel { get; } = Strings.AddOrg_Cancel;
}
