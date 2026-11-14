namespace DeviceIdentityInventory.Analysis;

public enum InventoryCommand
{
    Scope,
    ListClassicPaired,
    Inventory,
    Refused,
}

public sealed record InventoryOptions(string ClassicAepId, string? OutDirectory);

public sealed record InventoryArgumentResult(
    InventoryCommand Command,
    InventoryOptions? Options = null,
    string? Error = null);

/// <summary>Pure parser; identifier values are never copied into error messages.</summary>
public static class InventoryArguments
{
    public const string ListClassicPairedFlag = "--list-classic-paired";

    public static InventoryArgumentResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
            return new InventoryArgumentResult(InventoryCommand.Scope);

        if (args.Count == 1 && string.Equals(args[0], ListClassicPairedFlag, StringComparison.Ordinal))
            return new InventoryArgumentResult(InventoryCommand.ListClassicPaired);

        string? classicAepId = null;
        string? outDirectory = null;

        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--classic-aep-id":
                    if (classicAepId is not null)
                        return Refused("--classic-aep-id was given more than once.");
                    if (index + 1 >= args.Count || IsMissingValue(args[index + 1]))
                        return Refused("--classic-aep-id needs the exact id of a paired Classic AEP.");
                    classicAepId = args[++index];
                    break;

                case "--out":
                    if (outDirectory is not null)
                        return Refused("--out was given more than once.");
                    if (index + 1 >= args.Count || IsMissingValue(args[index + 1]))
                        return Refused("--out needs a folder path.");
                    outDirectory = args[++index];
                    break;

                default:
                    return Refused("An unknown or misplaced argument was given.");
            }
        }

        if (classicAepId is null)
            return Refused($"Use {ListClassicPairedFlag} to list candidate ids, or supply --classic-aep-id <id>.");

        return new InventoryArgumentResult(
            InventoryCommand.Inventory,
            new InventoryOptions(classicAepId, outDirectory));
    }

    private static bool IsMissingValue(string value)
        => string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal);

    private static InventoryArgumentResult Refused(string error)
        => new(InventoryCommand.Refused, Error: error);
}
