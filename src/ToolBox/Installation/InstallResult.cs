namespace ToolBox.Installation;

internal sealed record InstallResult(bool Changed, string VersionDirectory, string LogFile, bool VersionChanged);
