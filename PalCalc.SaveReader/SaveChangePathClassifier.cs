namespace PalCalc.SaveReader;

/// <summary>
/// Classifies filesystem paths before save-monitoring code decides whether to notify a save.
/// Watcher events are only hints, so this class deliberately has no watcher or UI side effects.
/// </summary>
internal static class SaveChangePathClassifier
{
    private static readonly string[] WorldFileNames = ["Level", "LevelMeta", "LocalData", "WorldOption"];

    public static bool IsRelevantStandardSavePath(string saveBasePath, string changedPath)
    {
        if (string.IsNullOrWhiteSpace(saveBasePath) || string.IsNullOrWhiteSpace(changedPath))
            return false;

        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(saveBasePath, changedPath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (Path.IsPathRooted(relativePath) || IsOutsideSaveRoot(relativePath))
            return false;

        var pathParts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return pathParts.Length switch
        {
            1 => IsWorldSaveFile(pathParts[0]),
            2 when pathParts[0].Equals("Players", StringComparison.OrdinalIgnoreCase) => IsPlayerSaveFile(pathParts[1]),
            _ => false,
        };
    }

    public static bool TryGetSaveIdFromWgsEntryName(string entryName, out string saveId)
    {
        saveId = null;
        if (string.IsNullOrWhiteSpace(entryName))
            return false;

        var firstSeparator = entryName.IndexOf('-');
        if (firstSeparator <= 0 || firstSeparator == entryName.Length - 1)
            return false;

        var candidateSaveId = entryName[..firstSeparator];
        if (IsXboxBackupName(candidateSaveId))
            return false;

        var logicalFileName = entryName[(firstSeparator + 1)..];
        if (!IsSupportedWgsLogicalFileName(logicalFileName))
            return false;

        saveId = candidateSaveId;
        return true;
    }

    private static bool IsOutsideSaveRoot(string relativePath) =>
        relativePath.Equals("..", StringComparison.Ordinal) ||
        relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);

    private static bool IsWorldSaveFile(string fileName) =>
        WorldFileNames.Any(worldFileName => IsSaveFileWithOptionalSuffix(fileName, worldFileName));

    private static bool IsSaveFileWithOptionalSuffix(string fileName, string baseName)
    {
        if (!fileName.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))
            return false;

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (fileNameWithoutExtension.Equals(baseName, StringComparison.OrdinalIgnoreCase))
            return true;

        return fileNameWithoutExtension.Length > baseName.Length &&
               fileNameWithoutExtension.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) &&
               (fileNameWithoutExtension[baseName.Length] is '-' or '_');
    }

    private static bool IsPlayerSaveFile(string fileName)
    {
        if (!fileName.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))
            return false;

        var playerId = Path.GetFileNameWithoutExtension(fileName);
        if (playerId.EndsWith("_dps", StringComparison.OrdinalIgnoreCase))
            playerId = playerId[..^"_dps".Length];

        return playerId.Length == 32 && playerId.All(Uri.IsHexDigit);
    }

    private static bool IsXboxBackupName(string saveId) =>
        saveId.StartsWith("Slot", StringComparison.OrdinalIgnoreCase) &&
        saveId.Length > "Slot".Length &&
        saveId["Slot".Length..].All(char.IsDigit);

    private static bool IsSupportedWgsLogicalFileName(string logicalFileName) =>
        WorldFileNames.Any(worldFileName =>
            logicalFileName.Equals(worldFileName, StringComparison.OrdinalIgnoreCase) ||
            logicalFileName.StartsWith($"{worldFileName}-", StringComparison.OrdinalIgnoreCase)) ||
        logicalFileName.StartsWith("Players-", StringComparison.OrdinalIgnoreCase);
}
