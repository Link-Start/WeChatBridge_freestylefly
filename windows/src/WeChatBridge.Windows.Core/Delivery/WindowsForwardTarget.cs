namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>
/// One paste destination on Windows — the counterpart of a macOS
/// <c>bundleIdentifier</c> looked up through Launch Services.
///
/// Windows has no bundle identity for unpackaged apps, so a target is a set of
/// clues resolved in order: running process names first (cheapest, and the
/// process may already be up), then installed executable paths, then an AUMID
/// for packaged apps. Every list is ordered best-guess first.
///
/// The exe names and install directories below are the macOS bundle-identifier
/// equivalents by name, not by measurement — several of these apps had not
/// shipped a stable Windows layout when this table was written. Entries are
/// injectable (<see cref="DeliveryEngine"/> takes overrides) so a correction
/// is a table edit, not a code change.
/// </summary>
public sealed record WindowsForwardTarget(
    /// <summary>Shown to the user in failures — mirrors <c>targetDisplayName</c>.</summary>
    string DisplayName,
    /// <summary>Process names without extension, e.g. <c>"Codex"</c>.</summary>
    IReadOnlyList<string> ProcessNames,
    /// <summary>
    /// Exe candidates: rooted paths (environment variables expanded), or bare
    /// file names resolved through PATH and App Paths. A bare name means
    /// "the app may be launchable even though we do not know where it lives".
    /// </summary>
    IReadOnlyList<string> ExeCandidates,
    /// <summary>
    /// Packaged-app id (<c>PackageFamilyName!AppId</c>) for MSIX targets that
    /// have no stable exe path; launched through <c>shell:AppsFolder</c>.
    /// </summary>
    string? Aumid = null,
    /// <summary>
    /// Ports the Doubao special case from ActionRunner.swift: the target can
    /// read local paths itself, so for ZIP archives the prompt and the path go
    /// as text instead of a file drop. Pending real-device verification on
    /// Windows — kept as a spec flag so it can be flipped per target.
    /// </summary>
    bool ReadsLocalArchives = false);

/// <summary>
/// A target after resolution: what the OS gave back for a
/// <see cref="WindowsForwardTarget"/>. Any field may be missing — a launched
/// app that has not opened a window yet has a process id but no handle, and a
/// packaged app launched by AUMID may expose neither.
/// </summary>
public sealed record ResolvedTarget(
    WindowsForwardTarget Spec,
    int? ProcessId,
    nint MainWindowHandle,
    string? ExePath)
{
    /// <summary>
    /// Whether <paramref name="hwnd"/>/<paramref name="processId"/> — the pair
    /// <c>GetForegroundWindow</c>/<c>GetWindowThreadProcessId</c> returned —
    /// belongs to this target. A handle match wins because a packaged app's
    /// process id is not always knowable; when nothing is known (AUMID launch),
    /// any foreground window that is not ours is accepted — best effort, since
    /// the user just watched the target open.
    /// </summary>
    public bool OwnsForeground(nint hwnd, uint processId, uint ownProcessId)
    {
        if (MainWindowHandle != 0 && hwnd == MainWindowHandle)
            return true;
        return ProcessId is { } pid ? processId == (uint)pid : processId != ownProcessId;
    }
}

/// <summary>
/// The fixed ShareAction → Windows target table, plus the custom-target mapping.
/// The table is data, not code: <see cref="DeliveryEngine"/> accepts an override
/// dictionary so tests — and future corrections to the guessed exe names —
/// never touch the engine.
/// </summary>
public static class WindowsForwardTargets
{
    public static IReadOnlyDictionary<ShareAction, WindowsForwardTarget> Defaults { get; } =
        new Dictionary<ShareAction, WindowsForwardTarget>
        {
            // Process names + install roots are best guesses pending on-device
            // verification; display names mirror ShareAction.TargetDisplayName.
            [ShareAction.Codex] = new(
                "Codex",
                ["Codex"],
                [
                    @"%LOCALAPPDATA%\Programs\Codex\Codex.exe",
                    @"%LOCALAPPDATA%\Codex\Codex.exe",
                    "Codex.exe",
                ]),
            [ShareAction.Claude] = new(
                "Claude",
                ["Claude"],
                [
                    @"%LOCALAPPDATA%\Programs\Claude\Claude.exe",
                    @"%LOCALAPPDATA%\AnthropicClaude\Claude.exe",
                    "Claude.exe",
                ]),
            [ShareAction.Doubao] = new(
                "豆包",
                ["Doubao"],
                [
                    @"%LOCALAPPDATA%\Programs\Doubao\Doubao.exe",
                    @"%LOCALAPPDATA%\Doubao\Doubao.exe",
                    "Doubao.exe",
                ],
                ReadsLocalArchives: true),
            [ShareAction.Qwen] = new(
                "千问办公",
                ["QwenWork", "千问办公"],
                [
                    @"%LOCALAPPDATA%\Programs\QwenWork\QwenWork.exe",
                    @"%LOCALAPPDATA%\千问办公\QwenWork.exe",
                    "QwenWork.exe",
                ]),
            [ShareAction.WorkBuddy] = new(
                "WorkBuddy",
                ["WorkBuddy"],
                [
                    @"%LOCALAPPDATA%\Programs\WorkBuddy\WorkBuddy.exe",
                    @"%LOCALAPPDATA%\WorkBuddy\WorkBuddy.exe",
                    "WorkBuddy.exe",
                ]),
            [ShareAction.WeSight] = new(
                "WeSight",
                ["WeSight"],
                [
                    @"%LOCALAPPDATA%\Programs\WeSight\WeSight.exe",
                    @"%LOCALAPPDATA%\WeSight\WeSight.exe",
                    "WeSight.exe",
                ]),
        };

    /// <summary>
    /// The spec behind a fixed share-menu entry. Obsidian and Clipboard return
    /// null deliberately: one is a vault write, not a paste, and the other is
    /// finished by the helper before the app ever runs.
    /// </summary>
    public static WindowsForwardTarget? For(ShareAction action) =>
        Defaults.TryGetValue(action, out var target) ? target : null;

    /// <summary>
    /// Rebuilds the custom target an intent carries — the two flat fields on
    /// <see cref="BatchIntent"/>, the way <c>BatchIntent.target</c> does on macOS.
    /// </summary>
    public static ForwardTarget? IntentTarget(BatchIntent? intent) =>
        intent?.TargetBundleIdentifier is { } id
            ? new ForwardTarget(id, intent.TargetDisplayName ?? id, intent.RequestedAt)
            : null;

    /// <summary>
    /// Turns a user's custom target into a spec. <c>BundleIdentifier</c> on
    /// Windows stores whatever identifies the app: an AUMID
    /// (<c>Family!App</c>), a full exe path, or a bare executable name.
    /// </summary>
    public static WindowsForwardTarget ForCustom(ForwardTarget target)
    {
        var id = target.BundleIdentifier;
        if (id.Contains('!'))
            return new WindowsForwardTarget(target.DisplayName, [], [], Aumid: id);

        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(id))
        {
            var stem = Path.GetFileNameWithoutExtension(id);
            return new WindowsForwardTarget(target.DisplayName, [stem], [id]);
        }

        return new WindowsForwardTarget(target.DisplayName, [id], [$"{id}.exe"]);
    }
}
