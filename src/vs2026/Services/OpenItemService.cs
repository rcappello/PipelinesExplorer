using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;
using PipelinesExplorer.VisualStudio.Resources;

namespace PipelinesExplorer.VisualStudio.Services;

/// <summary>Description of a YAML / script item the user asked to open from the tree.</summary>
public sealed class OpenTarget
{
    public required RepoLinkKey RepoLinkKey { get; init; }
    /// <summary>Pipeline-style reference (may include <c>$(System.DefaultWorkingDirectory)</c> or be relative).</summary>
    public required string RelativePath { get; init; }
    /// <summary>Cross-repo reference name (e.g. the <c>@repo</c> alias).</summary>
    public string? RepositoryAlias { get; init; }
    /// <summary>Optional dev.azure.com URL fallback.</summary>
    public string? WebUrl { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>1-based line number to reveal after opening.</summary>
    public int? SelectionLine { get; init; }
    /// <summary>Branch the YAML was read from. Used in warnings.</summary>
    public string? Branch { get; init; }
}

/// <summary>
/// Resolves <see cref="OpenTarget"/> references against a linked workspace folder
/// and opens the file in Visual Studio. When no link is set (or the file cannot
/// be found under it), prompts the user to either browse for the local clone
/// or open the resource in the browser. Mirrors the VS Code <c>OpenItemService</c>.
/// </summary>
public sealed class OpenItemService
{
    private readonly WorkspaceLinkService _links;
    private readonly LoggingService _logger;
    private readonly Func<VisualStudioExtensibility?> _extensibilityProvider;

    public OpenItemService(
        WorkspaceLinkService links,
        LoggingService logger,
        Func<VisualStudioExtensibility?> extensibilityProvider)
    {
        _links = links;
        _logger = logger;
        _extensibilityProvider = extensibilityProvider;
    }

    public async Task OpenAsync(OpenTarget target, CancellationToken cancellationToken = default)
    {
        var linked =
            (target.RepositoryAlias is not null ? _links.FindAnyByRepoKey(target.RepositoryAlias) : null)
            ?? _links.Get(target.RepoLinkKey);

        if (linked is null)
        {
            await PromptToLinkAsync(target, cancellationToken).ConfigureAwait(false);
            return;
        }

        var resolved = ResolveCandidate(linked, target.RelativePath);
        if (resolved is not null)
        {
            await OpenFileInVsAsync(resolved, target.SelectionLine, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.Warn($"Could not find {target.RelativePath} under {linked} for {target.DisplayName}");
        await ShowMissingFileFallbackAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenFileInVsAsync(string fsPath, int? selectionLine, CancellationToken ct)
    {
        var ext = _extensibilityProvider();
        if (ext is null)
        {
            // Fallback: use Process.Start with shell association.
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = fsPath, UseShellExecute = true }); }
            catch (Exception ex) { _logger.Error($"Shell open failed for {fsPath}", ex); }
            return;
        }
        try
        {
            if (selectionLine is int line && line > 0)
            {
                // Range coordinates are 0-based; SelectionLine is 1-based (matches the
                // VS Code OpenTarget contract). Selecting an empty range at column 0
                // just places the caret there without highlighting anything.
                var zero = line - 1;
                var range = new Microsoft.VisualStudio.RpcContracts.Utilities.Range(zero, 0, zero, 0);
                var options = new Microsoft.VisualStudio.RpcContracts.OpenDocument.OpenDocumentOptions(
                    selection: range,
                    ensureVisible: range,
                    ensureVisibleOptions: null,
                    isPreview: false,
                    activate: true,
                    logicalView: null,
                    projectId: null,
                    editorType: null);
                await ext.Documents().OpenDocumentAsync(new Uri(fsPath, UriKind.Absolute), options, ct).ConfigureAwait(false);
            }
            else
            {
                await ext.Documents().OpenDocumentAsync(new Uri(fsPath, UriKind.Absolute), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"OpenDocumentAsync failed for {fsPath}", ex);
            await ext.Shell().ShowPromptAsync(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.OpenItem_OpenFailed_Format, fsPath, ex.Message), PromptOptions.OK, ct).ConfigureAwait(false);
        }
    }

    private async Task ShowMissingFileFallbackAsync(OpenTarget target, CancellationToken ct)
    {
        var ext = _extensibilityProvider();
        if (ext is null) { return; }

        var msg = target.Branch is not null
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.OpenItem_FileNotFoundOnBranch_Format, target.RelativePath, target.Branch)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.OpenItem_FileNotFoundDefault_Format, target.RelativePath);

        var options = new PromptOptions<int> { DismissedReturns = -1, DefaultChoiceIndex = 0 };
        options.Choices.Add(Strings.OpenItem_RelinkWorkspace, 0);
        if (!string.IsNullOrEmpty(target.WebUrl)) { options.Choices.Add(Strings.OpenItem_OpenInBrowser, 1); }
        var pick = await ext.Shell().ShowPromptAsync(msg, options, ct).ConfigureAwait(false);
        if (pick == 0)
        {
            await PromptToLinkAsync(target, ct).ConfigureAwait(false);
        }
        else if (pick == 1 && !string.IsNullOrEmpty(target.WebUrl))
        {
            OpenInBrowser(target.WebUrl!);
        }
    }

    private async Task PromptToLinkAsync(OpenTarget target, CancellationToken ct)
    {
        var ext = _extensibilityProvider();
        if (ext is null) { return; }

        // When a web URL is known, give the user the choice between linking a
        // local clone or opening the resource in the browser before the OS
        // folder picker takes over. Mirrors the VS Code link prompt.
        if (!string.IsNullOrEmpty(target.WebUrl))
        {
            var options = new PromptOptions<int> { DismissedReturns = -1, DefaultChoiceIndex = 0 };
            options.Choices.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.OpenItem_PickFolder_Title_Format, target.RepoLinkKey.RepoKey), 0);
            options.Choices.Add(Strings.OpenItem_OpenInBrowser, 1);
            var pre = await ext.Shell().ShowPromptAsync(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.OpenItem_PickFolder_Title_Format, target.RepoLinkKey.RepoKey),
                options,
                ct).ConfigureAwait(false);
            if (pre == -1) { return; }
            if (pre == 1)
            {
                OpenInBrowser(target.WebUrl!);
                return;
            }
        }

        // Folder picker has to run on a UI (STA) thread.
        var picked = await PickFolderAsync(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.OpenItem_PickFolder_Title_Format, target.RepoLinkKey.RepoKey), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(picked)) { return; }

        _links.Set(target.RepoLinkKey, picked!);
        await OpenAsync(target, ct).ConfigureAwait(false);
    }

    /// <summary>Public wrapper used by the "Link workspace" command.</summary>
    public async Task<bool> PickAndLinkAsync(RepoLinkKey key, string repoLabel, CancellationToken ct = default)
    {
        var picked = await PickFolderAsync(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.OpenItem_PickFolder_Title_Format, repoLabel), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(picked)) { return false; }
        _links.Set(key, picked!);
        return true;
    }

    private static Task<string?> PickFolderAsync(string title, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            // Out-of-process Visual Studio extensions have no Application.MainWindow,
            // so OpenFolderDialog with no owner can be created behind every other
            // window (especially behind the VS shell that triggered the command).
            // Spin up a hidden, topmost host Window on this STA thread to serve as
            // the owner — that brings the file dialog to the foreground reliably.
            System.Windows.Window? owner = null;
            try
            {
                owner = new System.Windows.Window
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = System.Windows.WindowStyle.None,
                    ShowInTaskbar = false,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Topmost = true,
                    ShowActivated = true,
                };
                owner.Show();

                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = title,
                    Multiselect = false,
                };
                var ok = dlg.ShowDialog(owner);
                tcs.TrySetResult(ok == true ? dlg.FolderName : null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                try { owner?.Close(); } catch { /* ignore */ }
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ct.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    private static void OpenInBrowser(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* best-effort */ }
    }

    /// <summary>Strip Azure Pipelines variables that resolve to the repo root at runtime.</summary>
    private static string StripPipelineVariables(string p)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(p, @"\$\(\s*System\.DefaultWorkingDirectory\s*\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\$\(\s*Build\.SourcesDirectory\s*\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\$\(\s*Pipeline\.Workspace\s*\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\$\(\s*Agent\.BuildDirectory\s*\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return s.Replace('\\', '/').TrimStart('/').Trim();
    }

    private static string? ResolveCandidate(string rootFsPath, string reference)
    {
        var cleaned = StripPipelineVariables(reference);
        if (string.IsNullOrEmpty(cleaned)) { return null; }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(rootFsPath, cleaned)),
        };
        var noLeadingDots = System.Text.RegularExpressions.Regex.Replace(cleaned, @"^(?:\.\./)+", string.Empty);
        candidates.Add(Path.GetFullPath(Path.Combine(rootFsPath, noLeadingDots)));

        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) { return c; }
            }
            catch { /* ignore */ }
        }
        return null;
    }
}
